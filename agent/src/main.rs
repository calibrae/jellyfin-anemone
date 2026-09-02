use anyhow::{Context, Result};
use clap::Parser;
use tokio_util::sync::CancellationToken;
use tracing::{error, info, warn};
use tracing_subscriber::EnvFilter;

use polyp::config::{Cli, Config};
use polyp::hwaccel::{self, DetectInputs};
use polyp::job::JobManager;
use polyp::mount_local;
use polyp::probe::{check_mount, platform_string, probe_ffmpeg};
use polyp::ws;

#[tokio::main]
async fn main() -> Result<()> {
    let cli = Cli::parse();
    let cfg = Config::load(&cli).context("failed to load config")?;

    init_logging(&cfg.log_level);

    info!(
        name = %cfg.name,
        server_url = %cfg.server_url,
        ffmpeg = %cfg.ffmpeg,
        max_sessions = cfg.max_sessions,
        "polyp starting"
    );

    let caps = probe_ffmpeg(&cfg.ffmpeg)
        .await
        .with_context(|| format!("ffmpeg capability probe failed for {}", cfg.ffmpeg))?;
    info!(
        version = %caps.version,
        hwaccels = ?caps.hwaccels,
        n_encoders = caps.encoders.len(),
        n_decoders = caps.decoders.len(),
        n_filters = caps.filters.len(),
        "ffmpeg probe complete"
    );

    let mounts: Vec<_> = cfg
        .mounts
        .iter()
        .map(|m| {
            let mut status = check_mount(&m.path);
            if m.server_path != m.path {
                status.server_path = Some(m.server_path.clone());
            }
            status.local = mount_local::resolve_local(m.local, mount_local::detect_local(&m.path));
            status
        })
        .collect();
    for m in &mounts {
        if m.ok {
            info!(path = %m.path, server_path = ?m.server_path, local = ?m.local, "mount ok");
        } else {
            warn!(path = %m.path, server_path = ?m.server_path, local = ?m.local, "mount not ok (will still start)");
        }
    }

    let platform = platform_string();
    let detect_inputs = DetectInputs {
        platform: platform.clone(),
        hwaccels: caps.hwaccels.clone(),
        render_nodes: hwaccel::probe_render_nodes(),
        nvidia_present: hwaccel::probe_nvidia_present(),
    };
    let (hwaccel_val, hwaccel_device, hwaccel_reason) =
        hwaccel::resolve(cfg.hwaccel, cfg.hwaccel_device.clone(), &detect_inputs);
    info!(
        hwaccel = %hwaccel_val,
        hwaccel_device = ?hwaccel_device,
        reason = %hwaccel_reason,
        "hwaccel resolved"
    );

    let (job_manager, active_rx) = JobManager::new(cfg.ffmpeg.clone(), cfg.max_sessions);

    let shutdown = CancellationToken::new();
    spawn_signal_handlers(shutdown.clone());

    let ws_task = tokio::spawn(ws::run(
        cfg,
        caps,
        mounts,
        hwaccel_val,
        hwaccel_device,
        job_manager,
        active_rx,
        shutdown.clone(),
    ));

    match ws_task.await {
        Ok(Ok(())) => info!("polyp shut down cleanly"),
        Ok(Err(e)) => error!(error = %e, "ws client exited with error"),
        Err(e) => error!(error = %e, "ws task panicked"),
    }

    Ok(())
}

fn init_logging(log_level: &str) {
    let filter = EnvFilter::try_from_default_env().unwrap_or_else(|_| EnvFilter::new(log_level));
    tracing_subscriber::fmt().with_env_filter(filter).init();
}

fn spawn_signal_handlers(shutdown: CancellationToken) {
    #[cfg(unix)]
    {
        tokio::spawn(async move {
            use tokio::signal::unix::{signal, SignalKind};
            let mut sigterm = match signal(SignalKind::terminate()) {
                Ok(s) => s,
                Err(e) => {
                    error!(error = %e, "failed to install SIGTERM handler");
                    return;
                }
            };
            let mut sigint = match signal(SignalKind::interrupt()) {
                Ok(s) => s,
                Err(e) => {
                    error!(error = %e, "failed to install SIGINT handler");
                    return;
                }
            };
            tokio::select! {
                _ = sigterm.recv() => info!("received SIGTERM, shutting down"),
                _ = sigint.recv() => info!("received SIGINT, shutting down"),
            }
            shutdown.cancel();
        });
    }
    #[cfg(not(unix))]
    {
        tokio::spawn(async move {
            let _ = tokio::signal::ctrl_c().await;
            info!("received ctrl-c, shutting down");
            shutdown.cancel();
        });
    }
}
