//! End-to-end test: runs the real `jfc-mock-server` and `jfc-agent` binaries against a real
//! local ffmpeg, and checks the ingest output on disk. Skipped (with a clear message printed
//! to stdout -- run with `cargo test -- --nocapture` to see it) when no local ffmpeg binary can
//! be found, per the deliverables doc: "/opt/homebrew/bin/ffmpeg if present, else ffmpeg on
//! PATH".

use std::io::{BufRead, BufReader, Read, Write};
use std::process::{Child, Command, Stdio};
use std::sync::mpsc;
use std::time::{Duration, Instant};

/// Reads lines from a child's stdout/stderr on a background thread so the test can wait for a
/// specific line (or drain output for a failure message) without blocking on a full read.
struct LineReader {
    rx: mpsc::Receiver<String>,
    seen: std::sync::Mutex<Vec<String>>,
}

impl LineReader {
    fn spawn<R: Read + Send + 'static>(reader: R) -> Self {
        let (tx, rx) = mpsc::channel();
        std::thread::spawn(move || {
            for line in BufReader::new(reader).lines() {
                match line {
                    Ok(l) => {
                        if tx.send(l).is_err() {
                            break;
                        }
                    }
                    Err(_) => break,
                }
            }
        });
        Self {
            rx,
            seen: std::sync::Mutex::new(Vec::new()),
        }
    }

    /// Block until a line matching `pred` arrives, or `timeout` elapses. Every line seen along
    /// the way (matching or not) is recorded for [`Self::tail`].
    fn wait_for(&self, pred: impl Fn(&str) -> bool, timeout: Duration) -> Option<String> {
        let deadline = Instant::now() + timeout;
        loop {
            let remaining = deadline.saturating_duration_since(Instant::now());
            if remaining.is_zero() {
                return None;
            }
            match self.rx.recv_timeout(remaining) {
                Ok(line) => {
                    self.seen.lock().unwrap().push(line.clone());
                    if pred(&line) {
                        return Some(line);
                    }
                }
                Err(_) => return None,
            }
        }
    }

    fn tail(&self, n: usize) -> String {
        let seen = self.seen.lock().unwrap();
        seen.iter()
            .rev()
            .take(n)
            .rev()
            .cloned()
            .collect::<Vec<_>>()
            .join("\n")
    }
}

fn find_ffmpeg() -> Option<String> {
    if std::path::Path::new("/opt/homebrew/bin/ffmpeg").exists() {
        return Some("/opt/homebrew/bin/ffmpeg".to_string());
    }
    let on_path = Command::new("ffmpeg")
        .arg("-version")
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .status()
        .map(|s| s.success())
        .unwrap_or(false);
    if on_path {
        return Some("ffmpeg".to_string());
    }
    None
}

fn parse_listen_addr(line: &str) -> Option<String> {
    let idx = line.rfind("http://")?;
    Some(line[idx + "http://".len()..].trim().to_string())
}

fn spawn_agent(
    ffmpeg: &str,
    addr: &str,
    secret: &str,
    name: &str,
) -> (Child, LineReader, LineReader) {
    let mut agent = Command::new(env!("CARGO_BIN_EXE_jfc-agent"))
        .args([
            "--server-url",
            &format!("ws://{addr}/Cluster/agents/ws"),
            "--secret",
            secret,
            "--name",
            name,
            "--ffmpeg",
            ffmpeg,
            "--max-sessions",
            "3",
            "--log-level",
            "debug",
        ])
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .expect("spawn jfc-agent");
    let out = LineReader::spawn(agent.stdout.take().unwrap());
    let err = LineReader::spawn(agent.stderr.take().unwrap());
    (agent, out, err)
}

fn list_out_dir_names(dir: &std::path::Path) -> Vec<String> {
    std::fs::read_dir(dir)
        .unwrap()
        .map(|e| e.unwrap().file_name().into_string().unwrap())
        .collect()
}

#[test]
fn full_transcode_produces_expected_segments() {
    let Some(ffmpeg) = find_ffmpeg() else {
        eprintln!(
            "SKIPPING full_transcode_produces_expected_segments: no local ffmpeg binary found \
             (looked for /opt/homebrew/bin/ffmpeg and `ffmpeg` on PATH)"
        );
        return;
    };

    let out_dir = tempfile::tempdir().expect("tempdir");
    let secret = "e2e-test-secret-1";

    let mut mock = Command::new(env!("CARGO_BIN_EXE_jfc-mock-server"))
        .args([
            "--listen",
            "127.0.0.1:0",
            "--secret",
            secret,
            "--out-dir",
            out_dir.path().to_str().unwrap(),
            "--once",
        ])
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .expect("spawn jfc-mock-server");
    let mock_out = LineReader::spawn(mock.stdout.take().unwrap());
    let mock_err = LineReader::spawn(mock.stderr.take().unwrap());

    let listen_line = mock_out
        .wait_for(
            |l| l.contains("jfc-mock-server listening on"),
            Duration::from_secs(5),
        )
        .unwrap_or_else(|| {
            panic!(
                "mock-server never printed its listen address.\nstderr:\n{}",
                mock_err.tail(50)
            )
        });
    let addr = parse_listen_addr(&listen_line)
        .expect("could not parse listen address from mock-server output");

    let (mut agent, agent_out, agent_err) = spawn_agent(&ffmpeg, &addr, secret, "e2e-agent-1");

    let exit_line = mock_out.wait_for(|l| l.starts_with("[exit]"), Duration::from_secs(30));

    let mock_status = mock.wait().expect("wait on jfc-mock-server");

    let _ = agent.kill();
    let _ = agent.wait();

    let exit_line = exit_line.unwrap_or_else(|| {
        panic!(
            "never saw the job's exit frame within 30s.\nmock-server stdout tail:\n{}\nmock-server stderr tail:\n{}\nagent stdout tail:\n{}\nagent stderr tail:\n{}",
            mock_out.tail(50),
            mock_err.tail(50),
            agent_out.tail(50),
            agent_err.tail(50),
        )
    });
    assert!(
        exit_line.contains("code=0"),
        "job did not exit with code 0: {exit_line}"
    );
    assert!(
        mock_status.success(),
        "jfc-mock-server (--once) did not exit 0: {mock_status:?}"
    );

    let names = list_out_dir_names(out_dir.path());
    let ts_count = names
        .iter()
        .filter(|n| n.starts_with("testjob") && n.ends_with(".ts"))
        .count();
    let has_playlist = names.iter().any(|n| n == "testjob.m3u8");
    let has_part = names.iter().any(|n| n.ends_with(".part"));

    assert!(
        ts_count >= 8,
        "expected >= 8 testjob*.ts segments, got {ts_count}: {names:?}"
    );
    assert!(has_playlist, "expected testjob.m3u8 in out-dir: {names:?}");
    assert!(!has_part, "found leftover .part file(s): {names:?}");
}

#[test]
fn stdin_q_stops_job_early_with_fewer_segments() {
    let Some(ffmpeg) = find_ffmpeg() else {
        eprintln!(
            "SKIPPING stdin_q_stops_job_early_with_fewer_segments: no local ffmpeg binary found \
             (looked for /opt/homebrew/bin/ffmpeg and `ffmpeg` on PATH)"
        );
        return;
    };

    let out_dir = tempfile::tempdir().expect("tempdir");
    let secret = "e2e-test-secret-2";

    // The spec's built-in testsrc job doesn't use `-re` (real-time pacing), so on modern
    // hardware it finishes in well under a second -- there'd be nothing left to interrupt at
    // t=3s. Replay a near-identical job via --job-file with `-re` added so it takes ~12 real
    // seconds, making an early `q` at t=3s meaningful and reproducible.
    let job_file_argv: Vec<&str> = vec![
        "-y",
        "-re",
        "-f",
        "lavfi",
        "-i",
        "testsrc=duration=12:size=640x360:rate=25",
        "-f",
        "lavfi",
        "-i",
        "sine=frequency=440:duration=12",
        "-c:v",
        "libx264",
        "-preset",
        "veryfast",
        "-g",
        "50",
        "-keyint_min",
        "50",
        "-sc_threshold",
        "0",
        "-force_key_frames",
        "expr:gte(t,n_forced*2)",
        "-c:a",
        "aac",
        "-f",
        "hls",
        "-hls_time",
        "2",
        "-hls_list_size",
        "0",
        "-hls_playlist_type",
        "vod",
        "-start_number",
        "0",
        "-hls_segment_type",
        "mpegts",
        "-hls_segment_filename",
        "{ingest}/Cluster/ingest/{id}/qtest%d.ts",
        "-method",
        "PUT",
        "-http_persistent",
        "1",
        "-headers",
        "Authorization: Bearer {token}\r\n",
        "{ingest}/Cluster/ingest/{id}/qtest.m3u8",
    ];
    let mut job_file = tempfile::NamedTempFile::new().expect("create job-file tempfile");
    serde_json::to_writer(&mut job_file, &job_file_argv).expect("write job-file JSON");
    job_file.flush().expect("flush job-file");

    let mut mock = Command::new(env!("CARGO_BIN_EXE_jfc-mock-server"))
        .args([
            "--listen",
            "127.0.0.1:0",
            "--secret",
            secret,
            "--out-dir",
            out_dir.path().to_str().unwrap(),
            "--job-file",
            job_file.path().to_str().unwrap(),
        ])
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .expect("spawn jfc-mock-server");
    let mut mock_stdin = mock.stdin.take().unwrap();
    let mock_out = LineReader::spawn(mock.stdout.take().unwrap());
    let mock_err = LineReader::spawn(mock.stderr.take().unwrap());

    let listen_line = mock_out
        .wait_for(
            |l| l.contains("jfc-mock-server listening on"),
            Duration::from_secs(5),
        )
        .unwrap_or_else(|| {
            panic!(
                "mock-server never printed its listen address.\nstderr:\n{}",
                mock_err.tail(50)
            )
        });
    let addr = parse_listen_addr(&listen_line)
        .expect("could not parse listen address from mock-server output");

    let (mut agent, agent_out, agent_err) = spawn_agent(&ffmpeg, &addr, secret, "e2e-agent-2");

    mock_out
        .wait_for(|l| l.starts_with("[started]"), Duration::from_secs(10))
        .unwrap_or_else(|| {
            panic!(
                "job never started.\nmock stdout tail:\n{}\nagent stderr tail:\n{}",
                mock_out.tail(50),
                agent_err.tail(50)
            )
        });

    std::thread::sleep(Duration::from_secs(3));
    mock_stdin
        .write_all(b"q\n")
        .expect("write q to mock-server stdin");
    mock_stdin.flush().expect("flush q");

    let exit_line = mock_out.wait_for(|l| l.starts_with("[exit]"), Duration::from_secs(15));

    let _ = mock.kill();
    let _ = mock.wait();
    let _ = agent.kill();
    let _ = agent.wait();

    let exit_line = exit_line.unwrap_or_else(|| {
        panic!(
            "job did not exit after `q`.\nmock stdout tail:\n{}\nagent stdout tail:\n{}\nagent stderr tail:\n{}",
            mock_out.tail(50),
            agent_out.tail(50),
            agent_err.tail(50),
        )
    });
    assert!(
        exit_line.contains("code=0"),
        "job did not exit 0 after q: {exit_line}"
    );

    let names = list_out_dir_names(out_dir.path());
    let ts_count = names
        .iter()
        .filter(|n| n.starts_with("qtest") && n.ends_with(".ts"))
        .count();
    // A full 12s run at -hls_time 2 would produce ~6 segments; stopping ~3s in should yield
    // clearly fewer, but at least one (the job had time to produce output before `q` landed).
    assert!(
        ts_count >= 1,
        "expected at least 1 segment before the early stop, got {ts_count}: {names:?}"
    );
    assert!(ts_count < 6, "expected fewer than a full run's ~6 segments after stopping early, got {ts_count}: {names:?}");
}
