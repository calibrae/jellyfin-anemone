//! Control WebSocket client: connects to the plugin, does the `hello`/`welcome` handshake,
//! dispatches `job`/`stdin`/`kill`/`ping` frames, sends `status` on a timer and on job-count
//! change, and reconnects with exponential backoff + jitter. On any disconnect every running job
//! is SIGKILLed -- job liveness is control-connection liveness, per `PROTOCOL.md`.

use std::time::Duration;

use anyhow::{Context, Result};
use futures_util::{SinkExt, StreamExt};
use rand::Rng;
use tokio::sync::{mpsc, watch};
use tokio::time::MissedTickBehavior;
use tokio_tungstenite::tungstenite::client::ClientRequestBuilder;
use tokio_tungstenite::tungstenite::http::Uri;
use tokio_tungstenite::tungstenite::Message;
use tokio_tungstenite::{connect_async, MaybeTlsStream, WebSocketStream};
use tokio_util::sync::CancellationToken;
use tracing::{debug, info, warn};

use crate::config::Config;
use crate::job::{JobManager, JobSpec};
use crate::protocol::{
    peek_frame_type, AgentMessage, FfmpegCaps, HwAccel, MountStatus, ServerMessage,
};

const MIN_BACKOFF: Duration = Duration::from_secs(1);
const MAX_BACKOFF: Duration = Duration::from_secs(30);
const WELCOME_TIMEOUT: Duration = Duration::from_secs(10);
const DEFAULT_STATUS_INTERVAL_S: u64 = 10;

/// Drive the reconnect loop until `shutdown` is cancelled.
#[allow(clippy::too_many_arguments)]
pub async fn run(
    cfg: Config,
    caps: FfmpegCaps,
    mounts: Vec<MountStatus>,
    hwaccel: HwAccel,
    hwaccel_device: Option<String>,
    job_manager: JobManager,
    mut active_rx: watch::Receiver<u32>,
    shutdown: CancellationToken,
) -> Result<()> {
    let mut backoff = MIN_BACKOFF;

    while !shutdown.is_cancelled() {
        match connect_and_serve(
            &cfg,
            &caps,
            &mounts,
            hwaccel,
            hwaccel_device.as_deref(),
            &job_manager,
            &mut active_rx,
            &shutdown,
        )
        .await
        {
            ConnOutcome::Shutdown => break,
            ConnOutcome::WelcomedThenDisconnected => {
                // A full handshake succeeded at least once; reset backoff before retrying.
                backoff = MIN_BACKOFF;
            }
            ConnOutcome::NeverConnected => {}
        }

        if shutdown.is_cancelled() {
            break;
        }

        let jitter = Duration::from_millis(rand::thread_rng().gen_range(0..250));
        let sleep_for = backoff + jitter;
        info!(?sleep_for, "reconnecting after backoff");
        tokio::select! {
            _ = tokio::time::sleep(sleep_for) => {}
            _ = shutdown.cancelled() => break,
        }
        backoff = std::cmp::min(backoff * 2, MAX_BACKOFF);
    }

    job_manager.kill_all();
    Ok(())
}

enum ConnOutcome {
    Shutdown,
    WelcomedThenDisconnected,
    NeverConnected,
}

#[allow(clippy::too_many_arguments)]
async fn connect_and_serve(
    cfg: &Config,
    caps: &FfmpegCaps,
    mounts: &[MountStatus],
    hwaccel: HwAccel,
    hwaccel_device: Option<&str>,
    job_manager: &JobManager,
    active_rx: &mut watch::Receiver<u32>,
    shutdown: &CancellationToken,
) -> ConnOutcome {
    let request = match build_request(&cfg.server_url, &cfg.secret) {
        Ok(r) => r,
        Err(e) => {
            warn!(error = %e, "bad server_url, cannot connect");
            return ConnOutcome::NeverConnected;
        }
    };

    let connect_result = tokio::select! {
        r = connect_async(request) => r,
        _ = shutdown.cancelled() => return ConnOutcome::Shutdown,
    };
    let (ws_stream, _resp) = match connect_result {
        Ok(pair) => pair,
        Err(e) => {
            warn!(error = %e, server_url = %cfg.server_url, "connect failed");
            return ConnOutcome::NeverConnected;
        }
    };
    info!(server_url = %cfg.server_url, "connected");

    let (mut ws_write, mut ws_read) = ws_stream.split();
    let (out_tx, mut out_rx) = mpsc::unbounded_channel::<AgentMessage>();

    let writer = tokio::spawn(async move {
        while let Some(msg) = out_rx.recv().await {
            let text = match serde_json::to_string(&msg) {
                Ok(t) => t,
                Err(e) => {
                    warn!(error = %e, "failed to serialize outgoing frame");
                    continue;
                }
            };
            if let Err(e) = ws_write.send(Message::text(text)).await {
                warn!(error = %e, "ws send failed");
                break;
            }
        }
        let _ = ws_write.close().await;
    });

    let hello = AgentMessage::Hello {
        name: cfg.name.clone(),
        version: env!("CARGO_PKG_VERSION").to_string(),
        platform: crate::probe::platform_string(),
        ffmpeg: caps.clone(),
        hwaccel: Some(hwaccel),
        hwaccel_device: hwaccel_device.map(|s| s.to_string()),
        mounts: mounts.to_vec(),
        max_sessions: job_manager.max_sessions(),
    };
    if out_tx.send(hello).is_err() {
        drop(out_tx);
        let _ = writer.await;
        return ConnOutcome::NeverConnected;
    }

    let ping_interval_s = match wait_for_welcome(&mut ws_read).await {
        Ok(ping_interval_s) => ping_interval_s,
        Err(reason) => {
            warn!(%reason, "did not get welcome");
            drop(out_tx);
            let _ = writer.await;
            return ConnOutcome::NeverConnected;
        }
    };

    let outcome = serve_connection(
        &mut ws_read,
        &out_tx,
        job_manager,
        active_rx,
        shutdown,
        ping_interval_s,
    )
    .await;

    job_manager.kill_all();
    drop(out_tx);
    let _ = writer.await;
    outcome
}

/// Build the WS upgrade request carrying `Authorization: Bearer <secret>`.
fn build_request(server_url: &str, secret: &str) -> Result<ClientRequestBuilder> {
    let uri: Uri = server_url
        .parse()
        .with_context(|| format!("invalid server_url: {server_url}"))?;
    Ok(ClientRequestBuilder::new(uri).with_header("Authorization", format!("Bearer {secret}")))
}

type WsRead =
    futures_util::stream::SplitStream<WebSocketStream<MaybeTlsStream<tokio::net::TcpStream>>>;

/// Wait for `welcome` (returns `ping_interval_s`) or `reject`/close/timeout (returns an error).
async fn wait_for_welcome(ws_read: &mut WsRead) -> std::result::Result<u64, String> {
    let deadline = tokio::time::sleep(WELCOME_TIMEOUT);
    tokio::pin!(deadline);
    loop {
        tokio::select! {
            _ = &mut deadline => return Err("timed out waiting for welcome".to_string()),
            msg = ws_read.next() => {
                match msg {
                    Some(Ok(Message::Text(text))) => {
                        match serde_json::from_str::<ServerMessage>(text.as_str()) {
                            Ok(ServerMessage::Welcome { server, ingest_base, ping_interval_s }) => {
                                info!(server_version = %server.version, ffmpeg_version = %server.ffmpeg_version,
                                      %ingest_base, ping_interval_s, "welcomed");
                                return Ok(ping_interval_s);
                            }
                            Ok(ServerMessage::Reject { reason }) => {
                                return Err(format!("rejected: {reason}"));
                            }
                            Ok(_other) => {
                                // Something else arrived before welcome; keep waiting per protocol
                                // ("server answers welcome or reject and closes"), but don't hang forever.
                                continue;
                            }
                            Err(_) => {
                                let ty = peek_frame_type(text.as_str()).unwrap_or_else(|| "?".to_string());
                                warn!(frame_type = %ty, "unrecognized frame while awaiting welcome, ignoring");
                                continue;
                            }
                        }
                    }
                    Some(Ok(Message::Close(frame))) => {
                        return Err(format!("connection closed before welcome: {frame:?}"));
                    }
                    Some(Ok(_)) => continue,
                    Some(Err(e)) => return Err(format!("ws read error: {e}")),
                    None => return Err("connection ended before welcome".to_string()),
                }
            }
        }
    }
}

async fn serve_connection(
    ws_read: &mut WsRead,
    out_tx: &mpsc::UnboundedSender<AgentMessage>,
    job_manager: &JobManager,
    active_rx: &mut watch::Receiver<u32>,
    shutdown: &CancellationToken,
    ping_interval_s: u64,
) -> ConnOutcome {
    let status_period = Duration::from_secs(if ping_interval_s > 0 {
        ping_interval_s
    } else {
        DEFAULT_STATUS_INTERVAL_S
    });
    let mut status_ticker = tokio::time::interval(status_period);
    status_ticker.set_missed_tick_behavior(MissedTickBehavior::Delay);
    status_ticker.tick().await; // first tick fires immediately; send our initial status below instead

    send_status(out_tx, job_manager);

    loop {
        tokio::select! {
            biased;
            _ = shutdown.cancelled() => return ConnOutcome::Shutdown,
            msg = ws_read.next() => {
                match msg {
                    Some(Ok(Message::Text(text))) => handle_incoming(text.as_str(), job_manager, out_tx),
                    Some(Ok(Message::Close(frame))) => {
                        debug!(?frame, "server closed connection");
                        return ConnOutcome::WelcomedThenDisconnected;
                    }
                    Some(Ok(_)) => {}
                    Some(Err(e)) => {
                        warn!(error = %e, "ws read error");
                        return ConnOutcome::WelcomedThenDisconnected;
                    }
                    None => {
                        debug!("ws stream ended");
                        return ConnOutcome::WelcomedThenDisconnected;
                    }
                }
            }
            _ = status_ticker.tick() => {
                send_status(out_tx, job_manager);
            }
            changed = active_rx.changed() => {
                if changed.is_ok() {
                    send_status(out_tx, job_manager);
                } else {
                    return ConnOutcome::WelcomedThenDisconnected;
                }
            }
        }
    }
}

fn send_status(out_tx: &mpsc::UnboundedSender<AgentMessage>, job_manager: &JobManager) {
    let _ = out_tx.send(AgentMessage::Status {
        active: job_manager.active_count(),
        load: crate::load::sample(),
        mounts: None,
    });
}

fn handle_incoming(
    text: &str,
    job_manager: &JobManager,
    out_tx: &mpsc::UnboundedSender<AgentMessage>,
) {
    match serde_json::from_str::<ServerMessage>(text) {
        Ok(ServerMessage::Job {
            id,
            argv,
            token: _token,
            label,
            env,
        }) => {
            // The ingest bearer token is embedded by the server directly into argv's `-headers`
            // value (see PROTOCOL.md); the agent doesn't need it separately, ffmpeg carries it.
            job_manager.spawn(
                JobSpec {
                    id,
                    argv,
                    label,
                    env,
                },
                out_tx.clone(),
            );
        }
        Ok(ServerMessage::Stdin { id, data }) => {
            if !job_manager.send_stdin(&id, data.into_bytes()) {
                warn!(id = %id, "stdin for unknown job");
            }
        }
        Ok(ServerMessage::Kill { id }) => {
            if !job_manager.send_kill(&id) {
                warn!(id = %id, "kill for unknown job");
            }
        }
        Ok(ServerMessage::Ping {}) => {
            let _ = out_tx.send(AgentMessage::Pong {});
        }
        Ok(ServerMessage::Welcome { .. }) | Ok(ServerMessage::Reject { .. }) => {
            debug!("ignoring late welcome/reject after handshake");
        }
        Err(_) => {
            let ty = peek_frame_type(text).unwrap_or_else(|| "?".to_string());
            warn!(frame_type = %ty, "unknown or malformed frame, ignoring");
        }
    }
}
