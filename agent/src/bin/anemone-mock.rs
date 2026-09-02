//! `anemone-mock`: a fake Jellyfin Anemone plugin so `polyp` can be exercised end-to-end
//! without a real Jellyfin. Speaks the control WebSocket (`/Anemone/agents/ws`) and the ingest
//! PUT endpoint (`/Anemone/ingest/{job}/{name}`) per `PROTOCOL.md`.

use std::collections::HashMap;
use std::net::SocketAddr;
use std::path::PathBuf;
use std::sync::atomic::{AtomicBool, Ordering};
use std::sync::{Arc, Mutex};
use std::time::{Duration, Instant};

use anyhow::{Context, Result};
use axum::extract::ws::{Message as AxumMessage, WebSocket, WebSocketUpgrade};
use axum::extract::{Path, Request, State};
use axum::http::header::{AUTHORIZATION, CONNECTION};
use axum::http::{HeaderMap, HeaderValue, StatusCode};
use axum::response::{IntoResponse, Response};
use axum::routing::{get, put};
use axum::Router;
use clap::Parser;
use futures_util::{SinkExt, StreamExt};
use rand::Rng;
use tokio::io::{AsyncBufReadExt, AsyncWriteExt};
use tokio::sync::{mpsc, oneshot};
use tracing::{error, warn};

use polyp::protocol::{
    peek_frame_type, validate_ingest_filename, AgentMessage, ServerInfo, ServerMessage,
};

#[derive(Parser, Debug)]
#[command(
    name = "anemone-mock",
    about = "Fake Jellyfin Anemone plugin for testing polyp"
)]
struct Args {
    /// Address to listen on.
    #[arg(long, default_value = "127.0.0.1:8097")]
    listen: SocketAddr,

    /// Shared secret the agent must present as `Authorization: Bearer <secret>`.
    #[arg(long)]
    secret: String,

    /// Directory ingested segments/playlists land in.
    #[arg(long)]
    out_dir: PathBuf,

    /// Built-in job to send when an agent connects. Currently only "testsrc" is implemented.
    #[arg(long, default_value = "testsrc")]
    job: String,

    /// Path to a JSON file containing an argv array (strings), with `{id}`, `{token}`,
    /// `{ingest}` placeholders, to replay instead of the built-in testsrc job.
    #[arg(long)]
    job_file: Option<PathBuf>,

    /// Exit 0 after the sent job's `exit` frame.
    #[arg(long)]
    once: bool,
}

struct JobRecord {
    token: String,
    prefix: String,
}

struct AgentHandle {
    to_agent_tx: mpsc::UnboundedSender<ServerMessage>,
}

#[derive(Clone)]
struct AppState {
    secret: Arc<String>,
    out_dir: Arc<PathBuf>,
    ingest_base: Arc<String>,
    jobs: Arc<Mutex<HashMap<String, JobRecord>>>,
    agent: Arc<Mutex<Option<AgentHandle>>>,
    current_job: Arc<Mutex<Option<String>>>,
    job_sent: Arc<AtomicBool>,
    job_file_argv: Arc<Option<Vec<String>>>,
    once: bool,
    exit_tx: Arc<Mutex<Option<oneshot::Sender<()>>>>,
}

#[tokio::main]
async fn main() -> Result<()> {
    tracing_subscriber::fmt::init();
    let args = Args::parse();

    tokio::fs::create_dir_all(&args.out_dir)
        .await
        .with_context(|| format!("creating out-dir {}", args.out_dir.display()))?;

    let job_file_argv = match &args.job_file {
        Some(path) => {
            let text = std::fs::read_to_string(path)
                .with_context(|| format!("reading --job-file {}", path.display()))?;
            let argv: Vec<String> = serde_json::from_str(&text).with_context(|| {
                format!("parsing {} as a JSON array of strings", path.display())
            })?;
            Some(argv)
        }
        None => None,
    };

    let listener = tokio::net::TcpListener::bind(args.listen)
        .await
        .with_context(|| format!("binding {}", args.listen))?;
    let local_addr = listener.local_addr()?;
    let ingest_base = format!("http://{local_addr}");

    let (exit_tx, exit_rx) = oneshot::channel();

    let state = AppState {
        secret: Arc::new(args.secret.clone()),
        out_dir: Arc::new(args.out_dir.clone()),
        ingest_base: Arc::new(ingest_base),
        jobs: Arc::new(Mutex::new(HashMap::new())),
        agent: Arc::new(Mutex::new(None)),
        current_job: Arc::new(Mutex::new(None)),
        job_sent: Arc::new(AtomicBool::new(false)),
        job_file_argv: Arc::new(job_file_argv),
        once: args.once,
        exit_tx: Arc::new(Mutex::new(Some(exit_tx))),
    };

    println!("anemone-mock listening on http://{local_addr}");
    println!("  control:  ws://{local_addr}/Anemone/agents/ws");
    println!(
        "  ingest:   {}/Anemone/ingest/<job>/<name>",
        state.ingest_base
    );
    println!("  out-dir:  {}", state.out_dir.display());
    if !args.once {
        println!("  commands: q, p, u, kill, job (then Enter)");
    }

    let app = Router::new()
        .route("/Anemone/agents/ws", get(ws_upgrade_handler))
        .route("/Anemone/ingest/{job}/{name}", put(ingest_handler))
        .with_state(state.clone());

    tokio::spawn(async move {
        if let Err(e) = axum::serve(listener, app).await {
            error!(error = %e, "server error");
        }
    });

    if !args.once {
        spawn_stdin_task(state.clone());
    }

    if args.once {
        let _ = exit_rx.await;
        // let the writer task flush its close frame / final ingest writes settle
        tokio::time::sleep(Duration::from_millis(200)).await;
        std::process::exit(0);
    }

    tokio::signal::ctrl_c().await.ok();
    Ok(())
}

// --- control WebSocket ---

async fn ws_upgrade_handler(
    State(state): State<AppState>,
    headers: HeaderMap,
    ws: WebSocketUpgrade,
) -> Response {
    let expected = format!("Bearer {}", state.secret);
    let ok = headers
        .get(AUTHORIZATION)
        .and_then(|v| v.to_str().ok())
        .map(|v| v == expected)
        .unwrap_or(false);
    if !ok {
        warn!("rejected control connection: bad or missing Authorization header");
        return (StatusCode::FORBIDDEN, "bad secret").into_response();
    }
    ws.on_upgrade(move |socket| handle_agent_socket(socket, state))
}

async fn handle_agent_socket(socket: WebSocket, state: AppState) {
    let (mut sink, mut stream) = socket.split();

    let hello = loop {
        match stream.next().await {
            Some(Ok(AxumMessage::Text(text))) => {
                match serde_json::from_str::<AgentMessage>(&text) {
                    Ok(msg @ AgentMessage::Hello { .. }) => break Some(msg),
                    Ok(_other) => {
                        warn!("expected hello as first frame, ignoring frame and still waiting");
                    }
                    Err(_) => {
                        let ty = peek_frame_type(&text).unwrap_or_else(|| "?".to_string());
                        warn!(frame_type = %ty, "unrecognized frame while awaiting hello");
                    }
                }
            }
            Some(Ok(_)) => continue,
            Some(Err(e)) => {
                warn!(error = %e, "ws error while awaiting hello");
                return;
            }
            None => {
                warn!("connection closed before hello");
                return;
            }
        }
    };
    let AgentMessage::Hello {
        name,
        version,
        platform,
        ffmpeg,
        hwaccel,
        hwaccel_device,
        mounts,
        max_sessions,
    } = hello.expect("checked above")
    else {
        unreachable!()
    };

    println!("=== agent connected: {name} v{version} ({platform}) ===");
    println!("  ffmpeg:      {} ({})", ffmpeg.version, ffmpeg.path);
    println!("  hwaccels:    {:?}", ffmpeg.hwaccels);
    println!(
        "  codecs:      {} encoders, {} decoders, {} filters",
        ffmpeg.encoders.len(),
        ffmpeg.decoders.len(),
        ffmpeg.filters.len()
    );
    println!("  hwaccel:     {hwaccel:?} (device: {hwaccel_device:?})");
    println!("  mounts:      {mounts:?}");
    println!("  max_sessions:{max_sessions}");

    let welcome = ServerMessage::Welcome {
        server: ServerInfo {
            version: "10.11.0-mock".to_string(),
            ffmpeg_version: ffmpeg.version.clone(),
        },
        ingest_base: (*state.ingest_base).clone(),
        ping_interval_s: 10,
    };
    if send_ws(&mut sink, &welcome).await.is_err() {
        return;
    }

    let (to_agent_tx, mut to_agent_rx) = mpsc::unbounded_channel::<ServerMessage>();
    {
        let mut agent = state.agent.lock().expect("agent mutex poisoned");
        *agent = Some(AgentHandle {
            to_agent_tx: to_agent_tx.clone(),
        });
    }

    if !state.job_sent.swap(true, Ordering::SeqCst) {
        send_job(&state, &to_agent_tx).await;
    }

    let mut ping_ticker = tokio::time::interval(Duration::from_secs(10));
    ping_ticker.tick().await; // consume the immediate first tick

    loop {
        tokio::select! {
            msg = stream.next() => {
                match msg {
                    Some(Ok(AxumMessage::Text(text))) => handle_agent_frame(&text, &state),
                    Some(Ok(AxumMessage::Close(_))) => { println!("=== agent disconnected ==="); break; }
                    Some(Ok(_)) => {}
                    Some(Err(e)) => { warn!(error = %e, "ws read error"); break; }
                    None => { println!("=== agent disconnected ==="); break; }
                }
            }
            cmd = to_agent_rx.recv() => {
                match cmd {
                    Some(frame) => {
                        if send_ws(&mut sink, &frame).await.is_err() {
                            break;
                        }
                    }
                    None => break,
                }
            }
            _ = ping_ticker.tick() => {
                let _ = send_ws(&mut sink, &ServerMessage::Ping {}).await;
            }
        }
    }

    let mut agent = state.agent.lock().expect("agent mutex poisoned");
    *agent = None;
}

async fn send_ws(
    sink: &mut futures_util::stream::SplitSink<WebSocket, AxumMessage>,
    msg: &ServerMessage,
) -> Result<(), ()> {
    let text = match serde_json::to_string(msg) {
        Ok(t) => t,
        Err(e) => {
            error!(error = %e, "failed to serialize server frame");
            return Err(());
        }
    };
    sink.send(AxumMessage::Text(text.into()))
        .await
        .map_err(|e| {
            warn!(error = %e, "ws send failed");
        })
}

fn handle_agent_frame(text: &str, state: &AppState) {
    match serde_json::from_str::<AgentMessage>(text) {
        Ok(AgentMessage::Status {
            active,
            load,
            mounts,
        }) => {
            println!("[status] active={active} load={load:?} mounts={mounts:?}");
        }
        Ok(AgentMessage::Started { id, pid }) => {
            println!("[started] id={id} pid={pid}");
        }
        Ok(AgentMessage::Stderr { id, line }) => {
            println!("[stderr {id}] {line}");
        }
        Ok(AgentMessage::Exit { id, code, error }) => {
            println!("[exit] id={id} code={code} error={error:?}");
            let is_current = state
                .current_job
                .lock()
                .expect("current_job mutex poisoned")
                .as_deref()
                == Some(id.as_str());
            if is_current && state.once {
                if let Some(tx) = state.exit_tx.lock().expect("exit_tx mutex poisoned").take() {
                    let _ = tx.send(());
                }
            }
        }
        Ok(AgentMessage::Error { id, message }) => {
            println!("[error] id={id:?} message={message}");
        }
        Ok(AgentMessage::Pong {}) => {
            println!("[pong]");
        }
        Ok(AgentMessage::Hello { .. }) => {
            warn!("unexpected second hello, ignoring");
        }
        Err(_) => {
            let ty = peek_frame_type(text).unwrap_or_else(|| "?".to_string());
            warn!(frame_type = %ty, "unknown or malformed frame from agent, ignoring");
        }
    }
}

// --- interactive stdin ---

fn spawn_stdin_task(state: AppState) {
    tokio::spawn(async move {
        let stdin = tokio::io::stdin();
        let mut lines = tokio::io::BufReader::new(stdin).lines();
        loop {
            match lines.next_line().await {
                Ok(Some(line)) => handle_stdin_command(line.trim(), &state).await,
                Ok(None) => break,
                Err(e) => {
                    warn!(error = %e, "stdin read error");
                    break;
                }
            }
        }
    });
}

async fn handle_stdin_command(cmd: &str, state: &AppState) {
    let to_agent_tx = {
        let agent = state.agent.lock().expect("agent mutex poisoned");
        agent.as_ref().map(|a| a.to_agent_tx.clone())
    };
    let Some(to_agent_tx) = to_agent_tx else {
        println!("(no agent connected)");
        return;
    };

    match cmd {
        "q" => send_to_current(state, &to_agent_tx, |id| ServerMessage::Stdin {
            id,
            data: "q\n".to_string(),
        }),
        "p" => send_to_current(state, &to_agent_tx, |id| ServerMessage::Stdin {
            id,
            data: "p".to_string(),
        }),
        "u" => send_to_current(state, &to_agent_tx, |id| ServerMessage::Stdin {
            id,
            data: "u".to_string(),
        }),
        "kill" => send_to_current(state, &to_agent_tx, |id| ServerMessage::Kill { id }),
        "job" => send_job(state, &to_agent_tx).await,
        "" => {}
        other => println!("unknown command {other:?} (try: q, p, u, kill, job)"),
    }
}

fn send_to_current(
    state: &AppState,
    tx: &mpsc::UnboundedSender<ServerMessage>,
    build: impl FnOnce(String) -> ServerMessage,
) {
    let current = state
        .current_job
        .lock()
        .expect("current_job mutex poisoned")
        .clone();
    match current {
        Some(id) => {
            let _ = tx.send(build(id));
        }
        None => println!("(no job running)"),
    }
}

// --- job construction ---

async fn send_job(state: &AppState, to_agent_tx: &mpsc::UnboundedSender<ServerMessage>) {
    let id = random_hex(16);
    let token = random_hex(32);

    let (argv, prefix, label) = match state.job_file_argv.as_ref() {
        Some(template) => {
            let argv: Vec<String> = template
                .iter()
                .map(|s| {
                    s.replace("{id}", &id)
                        .replace("{token}", &token)
                        .replace("{ingest}", &state.ingest_base)
                })
                .collect();
            let prefix = derive_prefix(&argv).unwrap_or_else(|| {
                warn!("could not derive ingest prefix from --job-file argv; ingest PUTs for this job will be rejected");
                "job".to_string()
            });
            (argv, prefix, "job-file job".to_string())
        }
        None => (
            testsrc_argv(&state.ingest_base, &id, &token),
            "testjob".to_string(),
            "testsrc job".to_string(),
        ),
    };

    {
        let mut jobs = state.jobs.lock().expect("jobs mutex poisoned");
        jobs.insert(
            id.clone(),
            JobRecord {
                token: token.clone(),
                prefix,
            },
        );
    }
    {
        let mut current = state
            .current_job
            .lock()
            .expect("current_job mutex poisoned");
        *current = Some(id.clone());
    }

    println!("[job] sending {label} id={id}");
    let _ = to_agent_tx.send(ServerMessage::Job {
        id,
        argv,
        token,
        label,
        env: None,
    });
}

fn testsrc_argv(ingest_base: &str, id: &str, token: &str) -> Vec<String> {
    let seg = format!("{ingest_base}/Anemone/ingest/{id}/testjob%d.ts");
    let playlist = format!("{ingest_base}/Anemone/ingest/{id}/testjob.m3u8");
    let headers = format!("Authorization: Bearer {token}\r\n");
    vec![
        "-f".into(),
        "lavfi".into(),
        "-i".into(),
        "testsrc=duration=20:size=640x360:rate=25".into(),
        "-f".into(),
        "lavfi".into(),
        "-i".into(),
        "sine=frequency=440:duration=20".into(),
        "-c:v".into(),
        "libx264".into(),
        "-preset".into(),
        "veryfast".into(),
        "-g".into(),
        "50".into(),
        "-keyint_min".into(),
        "50".into(),
        "-sc_threshold".into(),
        "0".into(),
        "-force_key_frames".into(),
        "expr:gte(t,n_forced*2)".into(),
        "-c:a".into(),
        "aac".into(),
        "-f".into(),
        "hls".into(),
        "-hls_time".into(),
        "2".into(),
        "-hls_list_size".into(),
        "0".into(),
        "-hls_playlist_type".into(),
        "vod".into(),
        "-start_number".into(),
        "0".into(),
        "-hls_segment_type".into(),
        "mpegts".into(),
        "-hls_segment_filename".into(),
        seg,
        "-method".into(),
        "PUT".into(),
        "-http_persistent".into(),
        "1".into(),
        "-headers".into(),
        headers,
        "-y".into(),
        playlist,
    ]
}

/// Best-effort prefix derivation for `--job-file` argv: prefer `-hls_segment_filename`'s value
/// (basename up to the first `%`), else the last argv element (the playlist path) minus
/// `.m3u8`.
fn derive_prefix(argv: &[String]) -> Option<String> {
    for (i, a) in argv.iter().enumerate() {
        if a == "-hls_segment_filename" {
            if let Some(val) = argv.get(i + 1) {
                let basename = val.rsplit('/').next().unwrap_or(val);
                if let Some(pct_idx) = basename.find('%') {
                    return Some(basename[..pct_idx].to_string());
                }
            }
        }
    }
    let last = argv.last()?;
    let basename = last.rsplit('/').next().unwrap_or(last);
    basename.strip_suffix(".m3u8").map(|s| s.to_string())
}

fn random_hex(n_bytes: usize) -> String {
    let mut rng = rand::thread_rng();
    (0..n_bytes)
        .map(|_| format!("{:02x}", rng.gen::<u8>()))
        .collect()
}

// --- ingest PUT ---

async fn ingest_handler(
    State(state): State<AppState>,
    Path((job_id, name)): Path<(String, String)>,
    request: Request,
) -> Response {
    let start = Instant::now();
    let headers = request.headers().clone();

    let token = headers
        .get(AUTHORIZATION)
        .and_then(|v| v.to_str().ok())
        .and_then(|v| v.strip_prefix("Bearer "));

    let prefix = {
        let jobs = state.jobs.lock().expect("jobs mutex poisoned");
        match (token, jobs.get(&job_id)) {
            (Some(t), Some(rec)) if rec.token == t => Some(rec.prefix.clone()),
            _ => None,
        }
    };
    let Some(prefix) = prefix else {
        warn!(job = %job_id, name = %name, "ingest PUT rejected: unknown job or bad token");
        return forbidden_and_close();
    };

    if !validate_ingest_filename(&name, &prefix) {
        warn!(job = %job_id, name = %name, prefix = %prefix, "ingest PUT rejected: bad filename");
        return forbidden_and_close();
    }

    let part_path = state.out_dir.join(format!("{name}.part"));
    let final_path = state.out_dir.join(&name);

    let mut file = match tokio::fs::File::create(&part_path).await {
        Ok(f) => f,
        Err(e) => {
            error!(error = %e, path = %part_path.display(), "failed to create ingest file");
            return StatusCode::INTERNAL_SERVER_ERROR.into_response();
        }
    };

    let mut total: u64 = 0;
    let mut body_stream = request.into_body().into_data_stream();
    while let Some(chunk) = body_stream.next().await {
        let chunk = match chunk {
            Ok(c) => c,
            Err(e) => {
                error!(error = %e, "error reading ingest body");
                let _ = tokio::fs::remove_file(&part_path).await;
                return StatusCode::BAD_REQUEST.into_response();
            }
        };
        if let Err(e) = file.write_all(&chunk).await {
            error!(error = %e, "error writing ingest file");
            let _ = tokio::fs::remove_file(&part_path).await;
            return StatusCode::INTERNAL_SERVER_ERROR.into_response();
        }
        total += chunk.len() as u64;
    }
    if let Err(e) = file.flush().await {
        error!(error = %e, "error flushing ingest file");
        return StatusCode::INTERNAL_SERVER_ERROR.into_response();
    }
    drop(file);

    if let Err(e) = tokio::fs::rename(&part_path, &final_path).await {
        error!(error = %e, "error renaming ingest file into place");
        return StatusCode::INTERNAL_SERVER_ERROR.into_response();
    }

    tracing::info!(
        method = "PUT",
        name = %name,
        bytes = total,
        elapsed_ms = start.elapsed().as_millis() as u64,
        "ingested"
    );
    StatusCode::OK.into_response()
}

fn forbidden_and_close() -> Response {
    let mut resp = (StatusCode::FORBIDDEN, "").into_response();
    resp.headers_mut()
        .insert(CONNECTION, HeaderValue::from_static("close"));
    resp
}
