//! Wire protocol types and helpers, matching `PROTOCOL.md` at the repo root exactly.
//!
//! Two channels:
//! 1. Control: a WebSocket carrying one JSON object per text frame, `type`-discriminated.
//! 2. Ingest: ffmpeg on the agent PUTs HLS segments straight to the server; see
//!    [`validate_ingest_filename`] for the filename rule the server (and our mock server) applies.

use std::collections::HashMap;
use std::fmt;
use std::str::FromStr;

use serde::{Deserialize, Serialize};

/// Frames sent by the agent to the server.
// `Hello` is naturally the largest variant (it carries the full ffmpeg capability probe); these
// frames are constructed a handful of times per connection, not a hot path worth boxing fields
// over.
#[allow(clippy::large_enum_variant)]
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(tag = "type", rename_all = "snake_case")]
pub enum AgentMessage {
    Hello {
        name: String,
        version: String,
        platform: String,
        ffmpeg: FfmpegCaps,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        hwaccel: Option<HwAccel>,
        #[serde(default, skip_serializing_if = "Option::is_none")]
        hwaccel_device: Option<String>,
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
    /// Whether this ffmpeg honours the `p`/`u` interactive pause/resume keys Jellyfin's
    /// `TranscodingThrottler` uses to throttle a running transcode (protocol v2.2, see
    /// `PROTOCOL.md` "Throttling"). A jellyfin-ffmpeg patch (`0028-add-pause-support-for-ffmpeg-cli.patch`),
    /// absent from upstream ffmpeg, so it's probed rather than assumed -- see
    /// [`crate::probe::probe_ffmpeg`]. Unlike `hwaccel`/`server_path` above there's no genuine
    /// "unknown" state here: the agent always knows the answer once it has probed. `#[serde(default)]`
    /// exists purely so a `hello` frame from before this field existed still deserializes, defaulting
    /// to `false` -- the same "assume unsupported" conclusion a failed probe would reach.
    #[serde(default)]
    pub pause_keys: bool,
}

#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
pub struct MountStatus {
    pub path: String,
    pub ok: bool,
    /// What the Jellyfin server calls this same tree, when it differs from `path` (protocol v2,
    /// see `PROTOCOL.md` "Path mapping"). Omitted on the wire when equal to `path`.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub server_path: Option<String>,
    /// Whether this tree is on storage attached to the agent -- reading its source then costs no
    /// network round trip (protocol v2.1, see `PROTOCOL.md` "Placement inputs"). `true`/`false`
    /// when known; omitted on the wire when unknown -- never guessed.
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub local: Option<bool>,
}

/// Hardware-acceleration profile an agent's ffmpeg jobs should be built for (protocol v2, see
/// `PROTOCOL.md` "Hardware acceleration"). `None` is a valid, useful answer: a fast CPU with no
/// usable GPU still helps, it just gets `libx264`.
#[derive(Debug, Clone, Copy, PartialEq, Eq, Serialize, Deserialize)]
#[serde(rename_all = "snake_case")]
pub enum HwAccel {
    Videotoolbox,
    Nvenc,
    Qsv,
    Vaapi,
    Amf,
    Rkmpp,
    None,
}

impl HwAccel {
    pub fn as_str(&self) -> &'static str {
        match self {
            HwAccel::Videotoolbox => "videotoolbox",
            HwAccel::Nvenc => "nvenc",
            HwAccel::Qsv => "qsv",
            HwAccel::Vaapi => "vaapi",
            HwAccel::Amf => "amf",
            HwAccel::Rkmpp => "rkmpp",
            HwAccel::None => "none",
        }
    }
}

impl fmt::Display for HwAccel {
    fn fmt(&self, f: &mut fmt::Formatter<'_>) -> fmt::Result {
        f.write_str(self.as_str())
    }
}

impl FromStr for HwAccel {
    type Err = String;

    fn from_str(s: &str) -> Result<Self, Self::Err> {
        match s {
            "videotoolbox" => Ok(HwAccel::Videotoolbox),
            "nvenc" => Ok(HwAccel::Nvenc),
            "qsv" => Ok(HwAccel::Qsv),
            "vaapi" => Ok(HwAccel::Vaapi),
            "amf" => Ok(HwAccel::Amf),
            "rkmpp" => Ok(HwAccel::Rkmpp),
            "none" => Ok(HwAccel::None),
            other => Err(format!(
                "invalid hwaccel {other:?} (expected one of: videotoolbox, nvenc, qsv, vaapi, amf, rkmpp, none)"
            )),
        }
    }
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
                path: "/opt/anemone/ffmpeg".into(),
                version: "7.1.2-Jellyfin".into(),
                hwaccels: vec!["videotoolbox".into()],
                encoders: vec!["h264_videotoolbox".into(), "libx264".into()],
                decoders: vec!["h264".into(), "hevc".into()],
                filters: vec!["scale_vt".into(), "scale".into(), "overlay".into()],
                pause_keys: true,
            },
            hwaccel: None,
            hwaccel_device: None,
            mounts: vec![MountStatus {
                path: "/Volumes/data".into(),
                ok: true,
                server_path: None,
                local: None,
            }],
            max_sessions: 3,
        };
        let json = serde_json::to_string(&msg).unwrap();
        assert!(json.contains("\"type\":\"hello\""));
        // "hwaccels" (ffmpeg's reported list) legitimately appears; the top-level "hwaccel"/
        // "hwaccel_device" keys (both None here) must not.
        assert!(!json.contains("\"hwaccel\":"));
        assert!(!json.contains("\"hwaccel_device\":"));
        // pause_keys is always emitted (never Option-omitted): the agent always knows the answer
        // once it has probed.
        assert!(json.contains("\"pause_keys\":true"));
        let back: AgentMessage = serde_json::from_str(&json).unwrap();
        assert_eq!(msg, back);
    }

    #[test]
    fn round_trip_hello_with_hwaccel_and_server_path() {
        let msg = AgentMessage::Hello {
            name: "doppio".into(),
            version: "0.2.0".into(),
            platform: "linux-x86_64".into(),
            ffmpeg: FfmpegCaps {
                path: "/opt/anemone/ffmpeg".into(),
                version: "7.1.2-Jellyfin".into(),
                hwaccels: vec!["vaapi".into()],
                encoders: vec!["h264_vaapi".into(), "libx264".into()],
                decoders: vec!["h264".into(), "hevc".into()],
                filters: vec!["scale_vaapi".into(), "scale".into()],
                pause_keys: false,
            },
            hwaccel: Some(HwAccel::Vaapi),
            hwaccel_device: Some("/dev/dri/renderD128".into()),
            mounts: vec![MountStatus {
                path: "/mnt/media".into(),
                ok: true,
                server_path: Some("/Volumes/data".into()),
                local: Some(true),
            }],
            max_sessions: 3,
        };
        let json = serde_json::to_string(&msg).unwrap();
        assert!(json.contains("\"hwaccel\":\"vaapi\""));
        assert!(json.contains("\"hwaccel_device\":\"/dev/dri/renderD128\""));
        assert!(json.contains("\"server_path\":\"/Volumes/data\""));
        // `false` must be emitted, not dropped like an Option's None would be.
        assert!(json.contains("\"pause_keys\":false"));
        assert!(json.contains("\"local\":true"));
        let back: AgentMessage = serde_json::from_str(&json).unwrap();
        assert_eq!(msg, back);
    }

    #[test]
    fn hello_without_v2_fields_still_parses() {
        // A v1-shaped hello (no hwaccel/hwaccel_device on the frame, no server_path/local on a
        // mount) must still deserialize cleanly -- protocol v2 additions are backward compatible.
        let text = r#"{"type":"hello","name":"trish","version":"0.1.0","platform":"macos-arm64",
            "ffmpeg":{"path":"/opt/anemone/ffmpeg","version":"7.1.2-Jellyfin","hwaccels":["videotoolbox"],
                      "encoders":["h264_videotoolbox"],"decoders":["h264"],"filters":["scale_vt"]},
            "mounts":[{"path":"/Volumes/data","ok":true}],"max_sessions":3}"#;
        let msg: AgentMessage = serde_json::from_str(text).expect("v1 hello should still parse");
        match msg {
            AgentMessage::Hello {
                hwaccel,
                hwaccel_device,
                mounts,
                ffmpeg,
                ..
            } => {
                assert_eq!(hwaccel, None);
                assert_eq!(hwaccel_device, None);
                assert_eq!(mounts[0].server_path, None);
                assert_eq!(mounts[0].local, None);
                // protocol v2.2: a hello predating `pause_keys` still parses, defaulting to false.
                assert!(!ffmpeg.pause_keys);
            }
            other => panic!("expected Hello, got {other:?}"),
        }
    }

    #[test]
    fn hello_without_pause_keys_still_parses() {
        // Same backward-compatibility guarantee, isolated to just the new v2.2 field: a `hello`
        // whose `ffmpeg` object is otherwise fully v2-shaped (has hwaccel/server_path/local) but
        // predates `pause_keys` must still deserialize, defaulting to false.
        let text = r#"{"type":"hello","name":"trish","version":"0.1.0","platform":"macos-arm64",
            "ffmpeg":{"path":"/opt/anemone/ffmpeg","version":"7.1.2-Jellyfin","hwaccels":["videotoolbox"],
                      "encoders":["h264_videotoolbox"],"decoders":["h264"],"filters":["scale_vt"]},
            "hwaccel":"videotoolbox","mounts":[{"path":"/Volumes/data","ok":true,"local":true}],"max_sessions":3}"#;
        let msg: AgentMessage =
            serde_json::from_str(text).expect("hello without pause_keys should still parse");
        match msg {
            AgentMessage::Hello { ffmpeg, .. } => assert!(!ffmpeg.pause_keys),
            other => panic!("expected Hello, got {other:?}"),
        }
    }

    #[test]
    fn mount_status_omits_server_path_and_local_when_none() {
        let m = MountStatus {
            path: "/Volumes/data".into(),
            ok: true,
            server_path: None,
            local: None,
        };
        let json = serde_json::to_string(&m).unwrap();
        assert_eq!(json, r#"{"path":"/Volumes/data","ok":true}"#);
    }

    #[test]
    fn mount_status_includes_server_path_when_set() {
        let m = MountStatus {
            path: "/mnt/media".into(),
            ok: true,
            server_path: Some("/Volumes/data".into()),
            local: None,
        };
        let json = serde_json::to_string(&m).unwrap();
        assert_eq!(
            json,
            r#"{"path":"/mnt/media","ok":true,"server_path":"/Volumes/data"}"#
        );
    }

    #[test]
    fn mount_status_includes_local_true_when_set() {
        let m = MountStatus {
            path: "/mnt/das/data".into(),
            ok: true,
            server_path: None,
            local: Some(true),
        };
        let json = serde_json::to_string(&m).unwrap();
        assert_eq!(json, r#"{"path":"/mnt/das/data","ok":true,"local":true}"#);
        let back: MountStatus = serde_json::from_str(&json).unwrap();
        assert_eq!(m, back);
    }

    #[test]
    fn mount_status_includes_local_false_when_set() {
        // Some(false) must NOT be dropped like None is -- "not local" is meaningful information.
        let m = MountStatus {
            path: "/mnt/offload".into(),
            ok: true,
            server_path: None,
            local: Some(false),
        };
        let json = serde_json::to_string(&m).unwrap();
        assert!(json.contains("\"local\":false"));
        let back: MountStatus = serde_json::from_str(&json).unwrap();
        assert_eq!(m, back);
    }

    #[test]
    fn mount_status_without_local_field_still_parses() {
        // A server (or older agent build) that never sends "local" must still parse cleanly.
        let text = r#"{"path":"/Volumes/data","ok":true}"#;
        let m: MountStatus = serde_json::from_str(text).expect("mount status should parse");
        assert_eq!(m.local, None);
    }

    #[test]
    fn hwaccel_from_str_round_trips_all_variants() {
        for hw in [
            HwAccel::Videotoolbox,
            HwAccel::Nvenc,
            HwAccel::Qsv,
            HwAccel::Vaapi,
            HwAccel::Amf,
            HwAccel::Rkmpp,
            HwAccel::None,
        ] {
            let s = hw.to_string();
            let back: HwAccel = s.parse().unwrap();
            assert_eq!(hw, back);
        }
    }

    #[test]
    fn hwaccel_from_str_rejects_garbage() {
        assert!("bogus".parse::<HwAccel>().is_err());
    }

    #[test]
    fn hwaccel_serde_uses_lowercase_names() {
        assert_eq!(serde_json::to_string(&HwAccel::Vaapi).unwrap(), "\"vaapi\"");
        assert_eq!(
            serde_json::from_str::<HwAccel>("\"nvenc\"").unwrap(),
            HwAccel::Nvenc
        );
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
                server_path: None,
                local: Some(false),
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
