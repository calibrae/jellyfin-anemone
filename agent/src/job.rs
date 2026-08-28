//! Job supervisor: spawns ffmpeg per `job` frame, pipes `stdin` frames to the child, streams
//! `stderr` lines back, and enforces `max_sessions`. Job liveness is tied to the control
//! connection: [`JobManager::kill_all`] is called by `ws.rs` on every disconnect.

use std::collections::HashMap;
use std::process::Stdio;
use std::sync::{Arc, Mutex};

use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::sync::{mpsc, watch};
use tracing::{debug, info, warn};

use crate::protocol::{AgentMessage, LineSplitter};

/// A command sent to a single running job's supervising task.
#[derive(Debug)]
pub enum JobCommand {
    /// Raw bytes to write to the child's stdin, immediately, unbuffered.
    Stdin(Vec<u8>),
    /// SIGKILL the child now.
    Kill,
}

/// The fields of a `job` frame, decoupled from [`crate::protocol::ServerMessage`] so job.rs
/// doesn't need to match on the whole enum.
#[derive(Debug)]
pub struct JobSpec {
    pub id: String,
    pub argv: Vec<String>,
    pub label: String,
    pub env: Option<HashMap<String, String>>,
}

struct Inner {
    jobs: Mutex<HashMap<String, mpsc::UnboundedSender<JobCommand>>>,
    max_sessions: u32,
    ffmpeg_path: String,
    active_tx: watch::Sender<u32>,
}

/// Handle to the job supervisor. Cheap to clone; all clones share the same job table.
#[derive(Clone)]
pub struct JobManager {
    inner: Arc<Inner>,
}

impl JobManager {
    /// Returns the manager plus a watch receiver that fires whenever the active job count
    /// changes, so `ws.rs` can send an off-cycle `status` frame.
    pub fn new(ffmpeg_path: String, max_sessions: u32) -> (Self, watch::Receiver<u32>) {
        let (active_tx, active_rx) = watch::channel(0);
        let inner = Arc::new(Inner {
            jobs: Mutex::new(HashMap::new()),
            max_sessions,
            ffmpeg_path,
            active_tx,
        });
        (Self { inner }, active_rx)
    }

    pub fn active_count(&self) -> u32 {
        self.inner.jobs.lock().expect("jobs mutex poisoned").len() as u32
    }

    pub fn max_sessions(&self) -> u32 {
        self.inner.max_sessions
    }

    /// Start a job, or reject it over capacity. Sends `started`/`exit` frames on `out_tx` itself
    /// -- callers don't need to.
    pub fn spawn(&self, spec: JobSpec, out_tx: mpsc::UnboundedSender<AgentMessage>) {
        let over_capacity = {
            let jobs = self.inner.jobs.lock().expect("jobs mutex poisoned");
            jobs.len() as u32 >= self.inner.max_sessions
        };
        if over_capacity {
            warn!(id = %spec.id, max_sessions = self.inner.max_sessions, "job rejected: over capacity");
            let _ = out_tx.send(AgentMessage::Exit {
                id: spec.id,
                code: -2,
                error: Some("capacity".to_string()),
            });
            return;
        }

        let (cmd_tx, cmd_rx) = mpsc::unbounded_channel();
        {
            let mut jobs = self.inner.jobs.lock().expect("jobs mutex poisoned");
            jobs.insert(spec.id.clone(), cmd_tx);
        }
        self.publish_active();

        let ffmpeg_path = self.inner.ffmpeg_path.clone();
        let manager = self.clone();
        let id_for_finish = spec.id.clone();
        tokio::spawn(async move {
            run_job(ffmpeg_path, spec, cmd_rx, out_tx).await;
            manager.finish(&id_for_finish);
        });
    }

    fn finish(&self, id: &str) {
        {
            let mut jobs = self.inner.jobs.lock().expect("jobs mutex poisoned");
            jobs.remove(id);
        }
        self.publish_active();
    }

    fn publish_active(&self) {
        let active = self.active_count();
        let _ = self.inner.active_tx.send(active);
    }

    /// Forward raw stdin bytes to a running job. Returns `false` if the job is unknown (already
    /// exited, or never existed).
    pub fn send_stdin(&self, id: &str, data: Vec<u8>) -> bool {
        let jobs = self.inner.jobs.lock().expect("jobs mutex poisoned");
        match jobs.get(id) {
            Some(tx) => tx.send(JobCommand::Stdin(data)).is_ok(),
            None => false,
        }
    }

    /// SIGKILL a single running job. Returns `false` if the job is unknown.
    pub fn send_kill(&self, id: &str) -> bool {
        let jobs = self.inner.jobs.lock().expect("jobs mutex poisoned");
        match jobs.get(id) {
            Some(tx) => tx.send(JobCommand::Kill).is_ok(),
            None => false,
        }
    }

    /// SIGKILL every running job. Per `PROTOCOL.md`: "Job liveness is control-connection
    /// liveness" -- call this the moment the control WebSocket drops.
    pub fn kill_all(&self) {
        let jobs = self.inner.jobs.lock().expect("jobs mutex poisoned");
        for (id, tx) in jobs.iter() {
            debug!(id = %id, "killing job: control connection lost");
            let _ = tx.send(JobCommand::Kill);
        }
    }
}

async fn run_job(
    ffmpeg_path: String,
    spec: JobSpec,
    mut cmd_rx: mpsc::UnboundedReceiver<JobCommand>,
    out_tx: mpsc::UnboundedSender<AgentMessage>,
) {
    let id = spec.id.clone();
    debug!(id = %id, argv = ?spec.argv, "spawning ffmpeg");
    info!(id = %id, label = %spec.label, nargs = spec.argv.len(), "starting job");

    let mut cmd = tokio::process::Command::new(&ffmpeg_path);
    cmd.args(&spec.argv)
        .stdin(Stdio::piped())
        .stderr(Stdio::piped())
        .stdout(Stdio::null());
    if let Some(env) = &spec.env {
        cmd.envs(env);
    }

    let mut child = match cmd.spawn() {
        Ok(c) => c,
        Err(e) => {
            warn!(id = %id, error = %e, "failed to spawn ffmpeg");
            let _ = out_tx.send(AgentMessage::Error {
                id: Some(id.clone()),
                message: format!("spawn failed: {e}"),
            });
            let _ = out_tx.send(AgentMessage::Exit {
                id,
                code: -1,
                error: Some(format!("spawn failed: {e}")),
            });
            return;
        }
    };

    let pid = child.id().unwrap_or(0);
    let _ = out_tx.send(AgentMessage::Started {
        id: id.clone(),
        pid,
    });

    let mut stdin = child.stdin.take();
    let mut stderr = child.stderr.take().expect("stderr was piped");
    let mut splitter = LineSplitter::new();
    let mut buf = [0u8; 4096];
    let mut stderr_eof = false;

    let status = loop {
        tokio::select! {
            biased;
            cmd = cmd_rx.recv() => {
                match cmd {
                    Some(JobCommand::Stdin(data)) => {
                        if let Some(stdin) = stdin.as_mut() {
                            if let Err(e) = stdin.write_all(&data).await {
                                warn!(id = %id, error = %e, "stdin write failed");
                            } else if let Err(e) = stdin.flush().await {
                                warn!(id = %id, error = %e, "stdin flush failed");
                            }
                        }
                    }
                    Some(JobCommand::Kill) | None => {
                        debug!(id = %id, "sending SIGKILL");
                        let _ = child.start_kill();
                    }
                }
            }
            n = stderr.read(&mut buf), if !stderr_eof => {
                match n {
                    Ok(0) => stderr_eof = true,
                    Ok(n) => {
                        for line in splitter.feed(&buf[..n]) {
                            let _ = out_tx.send(AgentMessage::Stderr { id: id.clone(), line });
                        }
                    }
                    Err(_) => stderr_eof = true,
                }
            }
            result = child.wait() => break result,
        }
    };

    // Drain whatever stderr the process wrote right before exiting.
    loop {
        match stderr.read(&mut buf).await {
            Ok(0) | Err(_) => break,
            Ok(n) => {
                for line in splitter.feed(&buf[..n]) {
                    let _ = out_tx.send(AgentMessage::Stderr {
                        id: id.clone(),
                        line,
                    });
                }
            }
        }
    }
    if let Some(last) = splitter.flush() {
        let _ = out_tx.send(AgentMessage::Stderr {
            id: id.clone(),
            line: last,
        });
    }

    let (code, error) = match status {
        Ok(exit_status) => exit_code_and_error(&exit_status),
        Err(e) => (-1, Some(format!("wait() failed: {e}"))),
    };
    info!(id = %id, code, "job exited");
    let _ = out_tx.send(AgentMessage::Exit { id, code, error });
}

#[cfg(unix)]
fn exit_code_and_error(status: &std::process::ExitStatus) -> (i32, Option<String>) {
    use std::os::unix::process::ExitStatusExt;
    if let Some(code) = status.code() {
        (code, None)
    } else if let Some(sig) = status.signal() {
        (
            -1,
            Some(format!("killed by signal {} ({})", sig, signal_name(sig))),
        )
    } else {
        (-1, Some("process exited abnormally".to_string()))
    }
}

#[cfg(not(unix))]
fn exit_code_and_error(status: &std::process::ExitStatus) -> (i32, Option<String>) {
    match status.code() {
        Some(code) => (code, None),
        None => (-1, Some("process exited abnormally".to_string())),
    }
}

#[cfg(unix)]
fn signal_name(sig: i32) -> &'static str {
    match sig {
        1 => "SIGHUP",
        2 => "SIGINT",
        3 => "SIGQUIT",
        4 => "SIGILL",
        5 => "SIGTRAP",
        6 => "SIGABRT",
        7 => "SIGBUS",
        8 => "SIGFPE",
        9 => "SIGKILL",
        10 => "SIGUSR1",
        11 => "SIGSEGV",
        12 => "SIGUSR2",
        13 => "SIGPIPE",
        14 => "SIGALRM",
        15 => "SIGTERM",
        _ => "unknown",
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::time::Duration;
    use tokio::time::timeout;

    async fn recv(rx: &mut mpsc::UnboundedReceiver<AgentMessage>) -> AgentMessage {
        timeout(Duration::from_secs(5), rx.recv())
            .await
            .expect("timed out waiting for a message")
            .expect("channel closed unexpectedly")
    }

    async fn recv_matching<F>(
        rx: &mut mpsc::UnboundedReceiver<AgentMessage>,
        mut pred: F,
    ) -> AgentMessage
    where
        F: FnMut(&AgentMessage) -> bool,
    {
        loop {
            let msg = recv(rx).await;
            if pred(&msg) {
                return msg;
            }
        }
    }

    #[tokio::test]
    async fn spawns_and_reports_exit_code() {
        let (mgr, _active_rx) = JobManager::new("/bin/sh".to_string(), 3);
        let (out_tx, mut out_rx) = mpsc::unbounded_channel();
        mgr.spawn(
            JobSpec {
                id: "job1".into(),
                argv: vec!["-c".into(), "exit 3".into()],
                label: "test".into(),
                env: None,
            },
            out_tx,
        );

        let started =
            recv_matching(&mut out_rx, |m| matches!(m, AgentMessage::Started { .. })).await;
        assert!(matches!(started, AgentMessage::Started { pid, .. } if pid > 0));

        let exit = recv_matching(&mut out_rx, |m| matches!(m, AgentMessage::Exit { .. })).await;
        match exit {
            AgentMessage::Exit { id, code, error } => {
                assert_eq!(id, "job1");
                assert_eq!(code, 3);
                assert_eq!(error, None);
            }
            _ => unreachable!(),
        }
    }

    #[tokio::test]
    async fn over_capacity_is_rejected_without_starting() {
        let (mgr, _active_rx) = JobManager::new("/bin/sh".to_string(), 1);
        let (out_tx, mut out_rx) = mpsc::unbounded_channel();

        mgr.spawn(
            JobSpec {
                id: "long".into(),
                argv: vec!["-c".into(), "sleep 5".into()],
                label: "long".into(),
                env: None,
            },
            out_tx.clone(),
        );
        let _started =
            recv_matching(&mut out_rx, |m| matches!(m, AgentMessage::Started { .. })).await;

        mgr.spawn(
            JobSpec {
                id: "over".into(),
                argv: vec!["-c".into(), "exit 0".into()],
                label: "over".into(),
                env: None,
            },
            out_tx.clone(),
        );
        let rejected = recv(&mut out_rx).await;
        match rejected {
            AgentMessage::Exit { id, code, error } => {
                assert_eq!(id, "over");
                assert_eq!(code, -2);
                assert_eq!(error.as_deref(), Some("capacity"));
            }
            other => panic!("expected capacity Exit, got {other:?}"),
        }

        // clean up the long-running job
        assert!(mgr.send_kill("long"));
        let _ = recv_matching(&mut out_rx, |m| matches!(m, AgentMessage::Exit { .. })).await;
    }

    #[tokio::test]
    async fn kill_sends_sigkill_and_reports_signal() {
        let (mgr, _active_rx) = JobManager::new("/bin/sh".to_string(), 3);
        let (out_tx, mut out_rx) = mpsc::unbounded_channel();
        mgr.spawn(
            JobSpec {
                id: "killme".into(),
                argv: vec!["-c".into(), "sleep 30".into()],
                label: "killme".into(),
                env: None,
            },
            out_tx,
        );
        let _started =
            recv_matching(&mut out_rx, |m| matches!(m, AgentMessage::Started { .. })).await;

        assert!(mgr.send_kill("killme"));
        let exit = recv_matching(&mut out_rx, |m| matches!(m, AgentMessage::Exit { .. })).await;
        match exit {
            AgentMessage::Exit { id, code, error } => {
                assert_eq!(id, "killme");
                assert_eq!(code, -1);
                assert!(error.unwrap().contains("SIGKILL"));
            }
            _ => unreachable!(),
        }
    }

    #[tokio::test]
    async fn stdin_bytes_reach_the_child_unmodified() {
        let (mgr, _active_rx) = JobManager::new("/bin/sh".to_string(), 3);
        let (out_tx, mut out_rx) = mpsc::unbounded_channel();
        mgr.spawn(
            JobSpec {
                id: "echoer".into(),
                argv: vec!["-c".into(), "read line; echo \"got:$line\" 1>&2".into()],
                label: "echoer".into(),
                env: None,
            },
            out_tx,
        );
        let _started =
            recv_matching(&mut out_rx, |m| matches!(m, AgentMessage::Started { .. })).await;

        assert!(mgr.send_stdin("echoer", b"hello\n".to_vec()));

        let stderr_line =
            recv_matching(&mut out_rx, |m| matches!(m, AgentMessage::Stderr { .. })).await;
        match stderr_line {
            AgentMessage::Stderr { line, .. } => assert_eq!(line, "got:hello"),
            _ => unreachable!(),
        }
        let _ = recv_matching(&mut out_rx, |m| matches!(m, AgentMessage::Exit { .. })).await;
    }

    #[tokio::test]
    async fn send_to_unknown_job_returns_false() {
        let (mgr, _active_rx) = JobManager::new("/bin/sh".to_string(), 3);
        assert!(!mgr.send_stdin("nope", b"x".to_vec()));
        assert!(!mgr.send_kill("nope"));
    }

    #[tokio::test]
    async fn kill_all_kills_every_job() {
        let (mgr, _active_rx) = JobManager::new("/bin/sh".to_string(), 3);
        let (out_tx, mut out_rx) = mpsc::unbounded_channel();
        for id in ["a", "b"] {
            mgr.spawn(
                JobSpec {
                    id: id.into(),
                    argv: vec!["-c".into(), "sleep 30".into()],
                    label: id.into(),
                    env: None,
                },
                out_tx.clone(),
            );
        }
        let _s1 = recv_matching(&mut out_rx, |m| matches!(m, AgentMessage::Started { .. })).await;
        let _s2 = recv_matching(&mut out_rx, |m| matches!(m, AgentMessage::Started { .. })).await;
        assert_eq!(mgr.active_count(), 2);

        mgr.kill_all();

        let mut seen = std::collections::HashSet::new();
        for _ in 0..2 {
            let exit = recv_matching(&mut out_rx, |m| matches!(m, AgentMessage::Exit { .. })).await;
            if let AgentMessage::Exit { id, code, .. } = exit {
                assert_eq!(code, -1);
                seen.insert(id);
            }
        }
        assert_eq!(
            seen,
            std::collections::HashSet::from(["a".to_string(), "b".to_string()])
        );
    }

    #[tokio::test]
    async fn active_count_tracks_lifecycle() {
        let (mgr, mut active_rx) = JobManager::new("/bin/sh".to_string(), 3);
        assert_eq!(*active_rx.borrow(), 0);
        let (out_tx, mut out_rx) = mpsc::unbounded_channel();
        mgr.spawn(
            JobSpec {
                id: "j".into(),
                argv: vec!["-c".into(), "exit 0".into()],
                label: "j".into(),
                env: None,
            },
            out_tx,
        );
        active_rx.changed().await.unwrap();
        assert_eq!(*active_rx.borrow(), 1);
        let _exit = recv_matching(&mut out_rx, |m| matches!(m, AgentMessage::Exit { .. })).await;
        active_rx.changed().await.unwrap();
        assert_eq!(*active_rx.borrow(), 0);
    }
}
