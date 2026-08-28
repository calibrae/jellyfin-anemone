//! Wire protocol types and helpers, matching `PROTOCOL.md` at the repo root exactly.
//!
//! Two channels:
//! 1. Control: a WebSocket carrying one JSON object per text frame, `type`-discriminated.
//! 2. Ingest: ffmpeg on the agent PUTs HLS segments straight to the server; see
//!    [`validate_ingest_filename`] for the filename rule the server (and our mock server) applies.

use std::collections::HashMap;

use serde::{Deserialize, Serialize};

/// Frames sent by the agent to the server.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum AgentMessage {
    Hello {
        name: String,
        version: String,
        platform: String,
        ffmpeg: FfmpegCaps,
        mounts: Vec<MountStatus>,
        max_sessions: u32,
    },
    Status {
        active: u32,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        load: Option<f64>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        mounts: Option<Vec<MountStatus>>,
    },
    Started {
        id: String,
        pid: u32,
    },
    Stderr {
        id: String,
        line: String,
    },
    Exit {
        id: String,
        code: i32,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        error: Option<String>,
    },
    Error {
        #[serde(default, skip_serializing_if = "Option::is_none")]
        id: Option<String>,
        message: String,
    },
    Pong {},
}

/// Frames sent by the server to the agent.
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum ServerMessage {
    Welcome {
        server: ServerInfo,
        ingest_base: String,
        ping_interval_s: u64,
    },
    Reject {
        reason: String,
    },
    Job {
        id: String,
        argv: Vec<String>,
        token: String,
        label: String,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        env: Option<HashMap<String, String>>,
    },
    Stdin {
        id: String,
        data: String,
    },
    Kill {
        id: String,
    },
    Ping {},
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct FfmpegCaps {
    pub path: String,
    pub version: String,
    pub hwaccels: Vec<String>,
    pub encoders: Vec<String>,
    pub decoders: Vec<String>,
    pub filters: Vec<String>,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct MountStatus {
    pub path: String,
    pub ok: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct ServerInfo {
    pub version: String,
    pub ffmpeg_version: String,
}

/// Peek at the `type` field of a raw JSON frame without fully decoding it, for logging
/// unrecognized/malformed frames per `PROTOCOL.md`: "unknown `type` values are logged and ignored".
pub fn peek_frame_type(text: &str) -> Option<String> {
    let value: serde_json::Value = serde_json::from_str(text).ok()?;
    value.get("type")?.as_str().map(|s| s.to_string())
}

/// Validate an ingest PUT filename against a job's prefix, per `PROTOCOL.md`:
/// "`<prefix>` followed by `-?[0-9]+` and `.ts|.mp4|.m4s`, or `<prefix>.m3u8`", no path separators.
pub fn validate_ingest_filename(name: &str, prefix: &str) -> bool {
    if name.is_empty() || prefix.is_empty() {
        return false;
    }
    if name.contains('/') || name.contains('\\') || name.contains('\0') {
        return false;
    }
    let Some(rest) = name.strip_prefix(prefix) else {
        return false;
    };
    if rest == ".m3u8" {
        return true;
    }
    const EXTS: [&str; 3] = [".ts", ".mp4", ".m4s"];
    for ext in EXTS {
        if let Some(num_part) = rest.strip_suffix(ext) {
            let digits = num_part.strip_prefix('-').unwrap_or(num_part);
            if !digits.is_empty() && digits.bytes().all(|b| b.is_ascii_digit()) {
                return true;
            }
        }
    }
    false
}

/// Split a byte stream on `\n`, `\r`, and `\r\n` line terminators (ffmpeg emits progress lines
/// terminated by `\r`; Jellyfin's parser treats all three as line ends). Terminators are
/// stripped; empty lines are dropped. Call [`LineSplitter::flush`] at EOF to recover a trailing
/// unterminated line.
#[derive(Debug, Default)]
pub struct LineSplitter {
    buf: Vec<u8>,
}

impl LineSplitter {
    pub fn new() -> Self {
        Self { buf: Vec::new() }
    }

    /// Feed newly read bytes; returns any complete lines found (terminator stripped, empties
    /// dropped).
    pub fn feed(&mut self, data: &[u8]) -> Vec<String> {
        self.buf.extend_from_slice(data);
        let mut lines = Vec::new();
        let mut start = 0usize;
        let mut i = 0usize;
        while i < self.buf.len() {
            match self.buf[i] {
                b'\n' => {
                    lines.push(self.buf[start..i].to_vec());
                    i += 1;
                    start = i;
                }
                b'\r' => {
                    lines.push(self.buf[start..i].to_vec());
                    i += 1;
                    if i < self.buf.len() && self.buf[i] == b'\n' {
                        i += 1;
                    }
                    start = i;
                }
                _ => i += 1,
            }
        }
        self.buf.drain(0..start);
        lines
            .into_iter()
            .map(|b| String::from_utf8_lossy(&b).into_owned())
            .filter(|s| !s.is_empty())
            .collect()
    }

    /// Recover a trailing line that never saw a terminator (e.g. the process was killed
    /// mid-line). Returns `None` if there's nothing buffered or it's empty.
    pub fn flush(&mut self) -> Option<String> {
        if self.buf.is_empty() {
            return None;
        }
        let s = String::from_utf8_lossy(&self.buf).into_owned();
        self.buf.clear();
        if s.is_empty() {
            None
        } else {
            Some(s)
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn round_trip_hello() {
        let msg = AgentMessage::Hello {
            name: "trish".into(),
            version: "0.1.0".into(),
            platform: "macos-arm64".into(),
            ffmpeg: FfmpegCaps {
                path: "/opt/jfc/ffmpeg".into(),
                version: "7.1.2-Jellyfin".into(),
                hwaccels: vec!["videotoolbox".into()],
                encoders: vec!["h264_videotoolbox".into(), "libx264".into()],
                decoders: vec!["h264".into(), "hevc".into()],
                filters: vec!["scale_vt".into(), "scale".into(), "overlay".into()],
            },
            mounts: vec![MountStatus {
                path: "/Volumes/data".into(),
                ok: true,
            }],
            max_sessions: 3,
        };
        let json = serde_json::to_string(&msg).unwrap();
        assert!(json.contains("\"type\":\"hello\""));
        let back: AgentMessage = serde_json::from_str(&json).unwrap();
        assert_eq!(msg, back);
    }

    #[test]
    fn round_trip_status_minimal() {
        let msg = AgentMessage::Status {
            active: 2,
            load: None,
            mounts: None,
        };
        let json = serde_json::to_string(&msg).unwrap();
        assert_eq!(json, r#"{"type":"status","active":2}"#);
        let back: AgentMessage = serde_json::from_str(&json).unwrap();
        assert_eq!(msg, back);
    }

    #[test]
    fn round_trip_status_full() {
        let msg = AgentMessage::Status {
            active: 1,
            load: Some(0.5),
            mounts: Some(vec![MountStatus {
                path: "/Volumes/data".into(),
                ok: false,
            }]),
        };
        let json = serde_json::to_string(&msg).unwrap();
        let back: AgentMessage = serde_json::from_str(&json).unwrap();
        assert_eq!(msg, back);
    }

    #[test]
    fn round_trip_started() {
        let msg = AgentMessage::Started {
            id: "5f1c".into(),
            pid: 4242,
        };
        let back: AgentMessage =
            serde_json::from_str(&serde_json::to_string(&msg).unwrap()).unwrap();
        assert_eq!(msg, back);
    }

    #[test]
    fn round_trip_stderr() {
        let msg = AgentMessage::Stderr {
            id: "5f1c".into(),
            line: "frame=  120 fps= 60".into(),
        };
        let back: AgentMessage =
            serde_json::from_str(&serde_json::to_string(&msg).unwrap()).unwrap();
        assert_eq!(msg, back);
    }

    #[test]
    fn round_trip_exit_with_error() {
        let msg = AgentMessage::Exit {
            id: "5f1c".into(),
            code: -1,
            error: Some("signal 9 (SIGKILL)".into()),
        };
        let json = serde_json::to_string(&msg).unwrap();
        assert!(json.contains("\"error\""));
        let back: AgentMessage = serde_json::from_str(&json).unwrap();
        assert_eq!(msg, back);
    }

    #[test]
    fn round_trip_exit_without_error() {
        let msg = AgentMessage::Exit {
            id: "5f1c".into(),
            code: 0,
            error: None,
        };
        let json = serde_json::to_string(&msg).unwrap();
        assert!(!json.contains("error"));
        let back: AgentMessage = serde_json::from_str(&json).unwrap();
        assert_eq!(msg, back);
    }

    #[test]
    fn round_trip_error_frame() {
        let msg = AgentMessage::Error {
            id: Some("5f1c".into()),
            message: "spawn failed".into(),
        };
        let back: AgentMessage =
            serde_json::from_str(&serde_json::to_string(&msg).unwrap()).unwrap();
        assert_eq!(msg, back);

        let msg2 = AgentMessage::Error {
            id: None,
            message: "generic problem".into(),
        };
        let json2 = serde_json::to_string(&msg2).unwrap();
        assert!(!json2.contains("\"id\""));
        let back2: AgentMessage = serde_json::from_str(&json2).unwrap();
        assert_eq!(msg2, back2);
    }

    #[test]
    fn round_trip_pong() {
        let msg = AgentMessage::Pong {};
        let json = serde_json::to_string(&msg).unwrap();
        assert_eq!(json, r#"{"type":"pong"}"#);
        let back: AgentMessage = serde_json::from_str(&json).unwrap();
        assert_eq!(msg, back);
    }

    #[test]
    fn round_trip_welcome() {
        let msg = ServerMessage::Welcome {
            server: ServerInfo {
                version: "10.11.0".into(),
                ffmpeg_version: "7.1.2-Jellyfin".into(),
            },
            ingest_base: "http://10.240.0.1:8096".into(),
            ping_interval_s: 10,
        };
        let back: ServerMessage =
            serde_json::from_str(&serde_json::to_string(&msg).unwrap()).unwrap();
        assert_eq!(msg, back);
    }

    #[test]
    fn round_trip_reject() {
        let msg = ServerMessage::Reject {
            reason: "bad secret".into(),
        };
        let back: ServerMessage =
            serde_json::from_str(&serde_json::to_string(&msg).unwrap()).unwrap();
        assert_eq!(msg, back);
    }

    #[test]
    fn round_trip_job_with_env() {
        let mut env = HashMap::new();
        env.insert("FOO".to_string(), "bar".to_string());
        let msg = ServerMessage::Job {
            id: "5f1c".into(),
            argv: vec!["-f".into(), "hls".into()],
            token: "9kQ".into(),
            label: "Transcode a7858c".into(),
            env: Some(env),
        };
        let back: ServerMessage =
            serde_json::from_str(&serde_json::to_string(&msg).unwrap()).unwrap();
        assert_eq!(msg, back);
    }

    #[test]
    fn round_trip_job_without_env() {
        let msg = ServerMessage::Job {
            id: "5f1c".into(),
            argv: vec!["-f".into(), "hls".into()],
            token: "9kQ".into(),
            label: "Transcode a7858c".into(),
            env: None,
        };
        let json = serde_json::to_string(&msg).unwrap();
        assert!(!json.contains("\"env\""));
        let back: ServerMessage = serde_json::from_str(&json).unwrap();
        assert_eq!(msg, back);
    }

    #[test]
    fn round_trip_stdin() {
        let msg = ServerMessage::Stdin {
            id: "5f1c".into(),
            data: "q\n".into(),
        };
        let back: ServerMessage =
            serde_json::from_str(&serde_json::to_string(&msg).unwrap()).unwrap();
        assert_eq!(msg, back);
    }

    #[test]
    fn round_trip_kill() {
        let msg = ServerMessage::Kill { id: "5f1c".into() };
        let back: ServerMessage =
            serde_json::from_str(&serde_json::to_string(&msg).unwrap()).unwrap();
        assert_eq!(msg, back);
    }

    #[test]
    fn round_trip_ping() {
        let msg = ServerMessage::Ping {};
        let json = serde_json::to_string(&msg).unwrap();
        assert_eq!(json, r#"{"type":"ping"}"#);
        let back: ServerMessage = serde_json::from_str(&json).unwrap();
        assert_eq!(msg, back);
    }

    #[test]
    fn unknown_type_is_detected_not_panicking() {
        let text = r#"{"type":"frobnicate","foo":"bar"}"#;
        assert_eq!(peek_frame_type(text).as_deref(), Some("frobnicate"));
        assert!(serde_json::from_str::<ServerMessage>(text).is_err());
    }

    #[test]
    fn unknown_fields_are_ignored() {
        // extra field "extra_junk" should not break parsing of an otherwise-known frame
        let text = r#"{"type":"ping","extra_junk":123}"#;
        let msg: ServerMessage = serde_json::from_str(text).unwrap();
        assert_eq!(msg, ServerMessage::Ping {});
    }

    // --- ingest filename validation ---

    #[test]
    fn ingest_filename_ts_segment_valid() {
        assert!(validate_ingest_filename("testjob0.ts", "testjob"));
        assert!(validate_ingest_filename("testjob123.ts", "testjob"));
        assert!(validate_ingest_filename("a7858c-1.ts", "a7858c"));
    }

    #[test]
    fn ingest_filename_mp4_and_m4s_valid() {
        assert!(validate_ingest_filename("testjob0.mp4", "testjob"));
        assert!(validate_ingest_filename("testjob0.m4s", "testjob"));
    }

    #[test]
    fn ingest_filename_playlist_valid() {
        assert!(validate_ingest_filename("testjob.m3u8", "testjob"));
    }

    #[test]
    fn ingest_filename_rejects_wrong_prefix() {
        assert!(!validate_ingest_filename("other0.ts", "testjob"));
    }

    #[test]
    fn ingest_filename_rejects_path_traversal() {
        assert!(!validate_ingest_filename("../etc/passwd", "testjob"));
        assert!(!validate_ingest_filename(
            "testjob/../../etc/passwd.ts",
            "testjob"
        ));
        assert!(!validate_ingest_filename("sub/dir0.ts", "sub"));
    }

    #[test]
    fn ingest_filename_rejects_bad_extension() {
        assert!(!validate_ingest_filename("testjob0.exe", "testjob"));
        assert!(!validate_ingest_filename("testjob0.txt", "testjob"));
    }

    #[test]
    fn ingest_filename_rejects_non_numeric_suffix() {
        assert!(!validate_ingest_filename("testjobabc.ts", "testjob"));
        assert!(!validate_ingest_filename("testjob.ts", "testjob"));
        assert!(!validate_ingest_filename("testjob--1.ts", "testjob"));
        assert!(!validate_ingest_filename("testjob1a.ts", "testjob"));
    }

    #[test]
    fn ingest_filename_rejects_empty() {
        assert!(!validate_ingest_filename("", "testjob"));
        assert!(!validate_ingest_filename("testjob0.ts", ""));
    }

    // --- stderr line splitting ---

    #[test]
    fn line_splitter_lf() {
        let mut s = LineSplitter::new();
        let lines = s.feed(b"foo\nbar\n");
        assert_eq!(lines, vec!["foo", "bar"]);
        assert_eq!(s.flush(), None);
    }

    #[test]
    fn line_splitter_cr() {
        let mut s = LineSplitter::new();
        let lines = s.feed(b"frame=1\rframe=2\rframe=3\r");
        assert_eq!(lines, vec!["frame=1", "frame=2", "frame=3"]);
    }

    #[test]
    fn line_splitter_crlf() {
        let mut s = LineSplitter::new();
        let lines = s.feed(b"foo\r\nbar\r\n");
        assert_eq!(lines, vec!["foo", "bar"]);
    }

    #[test]
    fn line_splitter_mixed_terminators() {
        let mut s = LineSplitter::new();
        let lines = s.feed(b"a\nb\rc\r\nd");
        assert_eq!(lines, vec!["a", "b", "c"]);
        // "d" has no terminator yet
        assert_eq!(s.flush().as_deref(), Some("d"));
    }

    #[test]
    fn line_splitter_empty_lines_dropped() {
        let mut s = LineSplitter::new();
        let lines = s.feed(b"\n\nfoo\n\n");
        assert_eq!(lines, vec!["foo"]);
    }

    #[test]
    fn line_splitter_partial_feed_across_calls() {
        let mut s = LineSplitter::new();
        assert_eq!(s.feed(b"fra"), Vec::<String>::new());
        assert_eq!(s.feed(b"me=1\r"), vec!["frame=1"]);
        assert_eq!(s.feed(b"frame=2\n"), vec!["frame=2"]);
    }

    #[test]
    fn line_splitter_flush_on_empty_buffer() {
        let mut s = LineSplitter::new();
        assert_eq!(s.flush(), None);
    }
}
