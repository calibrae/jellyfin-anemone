//! Config: a TOML file (`--config`, default `/etc/polyp.toml`) with CLI overrides taking
//! precedence over file values, which take precedence over built-in defaults.

use std::path::{Path, PathBuf};

use anyhow::{Context, Result};
use clap::Parser;
use serde::Deserialize;

use crate::protocol::HwAccel;

#[derive(Parser, Debug)]
#[command(
    name = "polyp",
    about = "Runs ffmpeg transcodes on behalf of a Jellyfin cluster plugin"
)]
pub struct Cli {
    /// Path to the TOML config file.
    #[arg(long, default_value = "/etc/polyp.toml")]
    pub config: PathBuf,

    /// Control WebSocket URL, e.g. ws://10.240.0.1:8096/Anemone/agents/ws
    #[arg(long)]
    pub server_url: Option<String>,

    /// Shared secret sent as `Authorization: Bearer <secret>`.
    #[arg(long)]
    pub secret: Option<String>,

    /// Agent name reported in `hello`. Defaults to the machine's short hostname.
    #[arg(long)]
    pub name: Option<String>,

    /// Path to the jellyfin-ffmpeg binary.
    #[arg(long)]
    pub ffmpeg: Option<String>,

    /// Maximum concurrent transcode sessions.
    #[arg(long)]
    pub max_sessions: Option<u32>,

    /// Mount paths that must be readable (comma-separated, or repeat the flag). CLI mounts
    /// always mean an identical path on the agent and the server -- use the config file's
    /// `[[mounts]]` table form to declare a `server_path` mapping.
    #[arg(long, value_delimiter = ',')]
    pub mounts: Option<Vec<String>>,

    /// Hardware-acceleration profile: videotoolbox, nvenc, qsv, vaapi, amf, rkmpp, or none.
    /// Auto-detected when unset.
    #[arg(long)]
    pub hwaccel: Option<String>,

    /// Device the hwaccel profile needs, e.g. /dev/dri/renderD128 for vaapi/qsv on Linux.
    /// Defaults to the auto-detected render node when applicable.
    #[arg(long)]
    pub hwaccel_device: Option<String>,

    /// Log level: trace, debug, info, warn, error. Also honors RUST_LOG.
    #[arg(long)]
    pub log_level: Option<String>,
}

/// One `mounts` entry in the TOML file: either a bare string (identical path on agent and
/// server, the pre-v2 shape) or a table with an optional `server_path` mapping and an optional
/// `local` override.
#[derive(Debug, Clone, PartialEq, Eq, Deserialize)]
#[serde(untagged)]
enum MountEntry {
    Simple(String),
    Full {
        path: String,
        #[serde(default)]
        server_path: Option<String>,
        /// Wins over auto-detection when set -- an operator may know better than we do (e.g. an
        /// iSCSI LUN or a network block device that looks local, or vice versa). See
        /// `PROTOCOL.md` "Path mapping" -> `mounts[].local`.
        #[serde(default)]
        local: Option<bool>,
    },
}

impl MountEntry {
    fn into_spec(self) -> MountSpec {
        match self {
            MountEntry::Simple(path) => MountSpec {
                server_path: path.clone(),
                path,
                local: None,
            },
            MountEntry::Full {
                path,
                server_path,
                local,
            } => {
                let server_path = server_path.unwrap_or_else(|| path.clone());
                MountSpec {
                    path,
                    server_path,
                    local,
                }
            }
        }
    }
}

/// A configured mount, resolved to the pair the wire protocol wants: `path` (where the tree
/// lives on this agent) and `server_path` (what the Jellyfin server calls the same tree,
/// defaulting to `path` when the layout is identical), plus an optional `local` override that
/// wins over auto-detection when set (`None` means "detect").
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MountSpec {
    pub path: String,
    pub server_path: String,
    pub local: Option<bool>,
}

#[derive(Debug, Default, Deserialize)]
struct FileConfig {
    server_url: Option<String>,
    secret: Option<String>,
    name: Option<String>,
    ffmpeg: Option<String>,
    max_sessions: Option<u32>,
    mounts: Option<Vec<MountEntry>>,
    hwaccel: Option<String>,
    hwaccel_device: Option<String>,
    log_level: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Config {
    pub server_url: String,
    pub secret: String,
    pub name: String,
    pub ffmpeg: String,
    pub max_sessions: u32,
    pub mounts: Vec<MountSpec>,
    pub hwaccel: Option<HwAccel>,
    pub hwaccel_device: Option<String>,
    pub log_level: String,
}

pub const DEFAULT_MAX_SESSIONS: u32 = 3;
pub const DEFAULT_FFMPEG: &str = "ffmpeg";
pub const DEFAULT_LOG_LEVEL: &str = "info";

impl Config {
    pub fn load(cli: &Cli) -> Result<Config> {
        let file_cfg = load_file_config(&cli.config)?;
        Self::merge(cli, file_cfg)
    }

    fn merge(cli: &Cli, file_cfg: FileConfig) -> Result<Config> {
        let server_url = cli
            .server_url
            .clone()
            .or(file_cfg.server_url)
            .context("server_url must be set via --server-url or the config file")?;
        let secret = cli
            .secret
            .clone()
            .or(file_cfg.secret)
            .context("secret must be set via --secret or the config file")?;
        let name = cli
            .name
            .clone()
            .or(file_cfg.name)
            .unwrap_or_else(default_short_hostname);
        let ffmpeg = cli
            .ffmpeg
            .clone()
            .or(file_cfg.ffmpeg)
            .unwrap_or_else(|| DEFAULT_FFMPEG.to_string());
        let max_sessions = cli
            .max_sessions
            .or(file_cfg.max_sessions)
            .unwrap_or(DEFAULT_MAX_SESSIONS);
        // CLI mounts always mean an identical path on the agent and the server; the config
        // file's table form is the only way to set `server_path`.
        let mounts: Vec<MountSpec> = match &cli.mounts {
            Some(paths) => paths
                .iter()
                .map(|p| MountSpec {
                    path: p.clone(),
                    server_path: p.clone(),
                    local: None,
                })
                .collect(),
            None => file_cfg
                .mounts
                .map(|entries| entries.into_iter().map(MountEntry::into_spec).collect())
                .unwrap_or_default(),
        };
        let hwaccel = match cli.hwaccel.clone().or(file_cfg.hwaccel) {
            Some(s) => Some(
                s.parse::<HwAccel>()
                    .map_err(anyhow::Error::msg)
                    .with_context(|| "invalid hwaccel in config/CLI".to_string())?,
            ),
            None => None,
        };
        let hwaccel_device = cli.hwaccel_device.clone().or(file_cfg.hwaccel_device);
        let log_level = cli
            .log_level
            .clone()
            .or(file_cfg.log_level)
            .unwrap_or_else(|| DEFAULT_LOG_LEVEL.to_string());

        Ok(Config {
            server_url,
            secret,
            name,
            ffmpeg,
            max_sessions,
            mounts,
            hwaccel,
            hwaccel_device,
            log_level,
        })
    }
}

fn load_file_config(path: &Path) -> Result<FileConfig> {
    if !path.exists() {
        return Ok(FileConfig::default());
    }
    let text = std::fs::read_to_string(path)
        .with_context(|| format!("failed to read config file {}", path.display()))?;
    toml::from_str(&text).with_context(|| format!("failed to parse config file {}", path.display()))
}

fn default_short_hostname() -> String {
    hostname::get()
        .ok()
        .and_then(|h| h.into_string().ok())
        .map(|h| h.split('.').next().unwrap_or(&h).to_string())
        .filter(|h| !h.is_empty())
        .unwrap_or_else(|| "polyp".to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn empty_cli() -> Cli {
        Cli {
            config: PathBuf::from("/nonexistent/polyp.toml"),
            server_url: None,
            secret: None,
            name: None,
            ffmpeg: None,
            max_sessions: None,
            mounts: None,
            hwaccel: None,
            hwaccel_device: None,
            log_level: None,
        }
    }

    #[test]
    fn cli_overrides_file() {
        let mut cli = empty_cli();
        cli.server_url = Some("ws://cli/".into());
        cli.secret = Some("cli-secret".into());
        let file_cfg = FileConfig {
            server_url: Some("ws://file/".into()),
            secret: Some("file-secret".into()),
            ..Default::default()
        };
        let cfg = Config::merge(&cli, file_cfg).unwrap();
        assert_eq!(cfg.server_url, "ws://cli/");
        assert_eq!(cfg.secret, "cli-secret");
    }

    #[test]
    fn file_used_when_cli_absent() {
        let cli = empty_cli();
        let file_cfg = FileConfig {
            server_url: Some("ws://file/".into()),
            secret: Some("file-secret".into()),
            max_sessions: Some(7),
            ..Default::default()
        };
        let cfg = Config::merge(&cli, file_cfg).unwrap();
        assert_eq!(cfg.server_url, "ws://file/");
        assert_eq!(cfg.max_sessions, 7);
    }

    #[test]
    fn defaults_applied() {
        let cli = empty_cli();
        let file_cfg = FileConfig {
            server_url: Some("ws://file/".into()),
            secret: Some("file-secret".into()),
            ..Default::default()
        };
        let cfg = Config::merge(&cli, file_cfg).unwrap();
        assert_eq!(cfg.max_sessions, DEFAULT_MAX_SESSIONS);
        assert_eq!(cfg.ffmpeg, DEFAULT_FFMPEG);
        assert_eq!(cfg.log_level, DEFAULT_LOG_LEVEL);
        assert!(cfg.mounts.is_empty());
        assert!(!cfg.name.is_empty());
    }

    #[test]
    fn missing_server_url_and_secret_errors() {
        let cli = empty_cli();
        let err = Config::merge(&cli, FileConfig::default()).unwrap_err();
        assert!(err.to_string().contains("server_url"));
    }

    #[test]
    fn parses_example_toml_shape() {
        let toml_text = r#"
            server_url = "ws://10.240.0.1:8096/Anemone/agents/ws"
            secret = "sekrit"
            name = "trish"
            ffmpeg = "/opt/anemone/ffmpeg"
            max_sessions = 3
            mounts = ["/Volumes/data"]
            log_level = "info"
        "#;
        let file_cfg: FileConfig = toml::from_str(toml_text).unwrap();
        assert_eq!(
            file_cfg.server_url.as_deref(),
            Some("ws://10.240.0.1:8096/Anemone/agents/ws")
        );
        assert_eq!(
            file_cfg.mounts.as_deref(),
            Some(&[MountEntry::Simple("/Volumes/data".to_string())][..])
        );
    }

    // --- mount shapes ---

    #[test]
    fn bare_string_mount_round_trips_with_matching_server_path() {
        let toml_text = r#"mounts = ["/Volumes/data"]"#;
        let file_cfg: FileConfig = toml::from_str(toml_text).unwrap();
        let mut cli = empty_cli();
        cli.server_url = Some("ws://file/".into());
        cli.secret = Some("s".into());
        let cfg = Config::merge(&cli, file_cfg).unwrap();
        assert_eq!(
            cfg.mounts,
            vec![MountSpec {
                path: "/Volumes/data".into(),
                server_path: "/Volumes/data".into(),
                local: None,
            }]
        );
    }

    #[test]
    fn table_mount_with_explicit_server_path() {
        let toml_text = r#"
            [[mounts]]
            path = "/mnt/media"
            server_path = "/Volumes/data"
        "#;
        let file_cfg: FileConfig = toml::from_str(toml_text).unwrap();
        let mut cli = empty_cli();
        cli.server_url = Some("ws://file/".into());
        cli.secret = Some("s".into());
        let cfg = Config::merge(&cli, file_cfg).unwrap();
        assert_eq!(
            cfg.mounts,
            vec![MountSpec {
                path: "/mnt/media".into(),
                server_path: "/Volumes/data".into(),
                local: None,
            }]
        );
    }

    #[test]
    fn table_mount_without_server_path_defaults_to_path() {
        let toml_text = r#"
            [[mounts]]
            path = "/mnt/media"
        "#;
        let file_cfg: FileConfig = toml::from_str(toml_text).unwrap();
        let mut cli = empty_cli();
        cli.server_url = Some("ws://file/".into());
        cli.secret = Some("s".into());
        let cfg = Config::merge(&cli, file_cfg).unwrap();
        assert_eq!(
            cfg.mounts,
            vec![MountSpec {
                path: "/mnt/media".into(),
                server_path: "/mnt/media".into(),
                local: None,
            }]
        );
    }

    #[test]
    fn mixed_bare_string_and_table_mounts() {
        let toml_text = r#"mounts = ["/Volumes/data", { path = "/mnt/media", server_path = "/Volumes/other" }]"#;
        let file_cfg: FileConfig = toml::from_str(toml_text).unwrap();
        let mut cli = empty_cli();
        cli.server_url = Some("ws://file/".into());
        cli.secret = Some("s".into());
        let cfg = Config::merge(&cli, file_cfg).unwrap();
        assert_eq!(
            cfg.mounts,
            vec![
                MountSpec {
                    path: "/Volumes/data".into(),
                    server_path: "/Volumes/data".into(),
                    local: None,
                },
                MountSpec {
                    path: "/mnt/media".into(),
                    server_path: "/Volumes/other".into(),
                    local: None,
                },
            ]
        );
    }

    #[test]
    fn cli_mounts_mean_identical_paths_and_override_file() {
        let mut cli = empty_cli();
        cli.server_url = Some("ws://file/".into());
        cli.secret = Some("s".into());
        cli.mounts = Some(vec!["/a".into(), "/b".into()]);
        let file_cfg = FileConfig {
            mounts: Some(vec![MountEntry::Full {
                path: "/mnt/media".into(),
                server_path: Some("/Volumes/data".into()),
                local: Some(true),
            }]),
            ..Default::default()
        };
        let cfg = Config::merge(&cli, file_cfg).unwrap();
        assert_eq!(
            cfg.mounts,
            vec![
                MountSpec {
                    path: "/a".into(),
                    server_path: "/a".into(),
                    local: None,
                },
                MountSpec {
                    path: "/b".into(),
                    server_path: "/b".into(),
                    local: None,
                },
            ]
        );
    }

    #[test]
    fn table_mount_with_local_true_override() {
        let toml_text = r#"
            [[mounts]]
            path = "/mnt/das/data"
            local = true
        "#;
        let file_cfg: FileConfig = toml::from_str(toml_text).unwrap();
        let mut cli = empty_cli();
        cli.server_url = Some("ws://file/".into());
        cli.secret = Some("s".into());
        let cfg = Config::merge(&cli, file_cfg).unwrap();
        assert_eq!(
            cfg.mounts,
            vec![MountSpec {
                path: "/mnt/das/data".into(),
                server_path: "/mnt/das/data".into(),
                local: Some(true),
            }]
        );
    }

    #[test]
    fn table_mount_with_local_false_override() {
        let toml_text = r#"
            [[mounts]]
            path = "/mnt/offload"
            local = false
        "#;
        let file_cfg: FileConfig = toml::from_str(toml_text).unwrap();
        let mut cli = empty_cli();
        cli.server_url = Some("ws://file/".into());
        cli.secret = Some("s".into());
        let cfg = Config::merge(&cli, file_cfg).unwrap();
        assert_eq!(cfg.mounts[0].local, Some(false));
    }

    #[test]
    fn table_mount_without_local_leaves_it_unset_for_detection() {
        let toml_text = r#"
            [[mounts]]
            path = "/mnt/media"
        "#;
        let file_cfg: FileConfig = toml::from_str(toml_text).unwrap();
        let mut cli = empty_cli();
        cli.server_url = Some("ws://file/".into());
        cli.secret = Some("s".into());
        let cfg = Config::merge(&cli, file_cfg).unwrap();
        assert_eq!(cfg.mounts[0].local, None);
    }

    #[test]
    fn bare_string_mount_never_sets_local_override() {
        let toml_text = r#"mounts = ["/Volumes/data"]"#;
        let file_cfg: FileConfig = toml::from_str(toml_text).unwrap();
        let mut cli = empty_cli();
        cli.server_url = Some("ws://file/".into());
        cli.secret = Some("s".into());
        let cfg = Config::merge(&cli, file_cfg).unwrap();
        assert_eq!(cfg.mounts[0].local, None);
    }

    // --- hwaccel ---

    #[test]
    fn hwaccel_unset_by_default() {
        let mut cli = empty_cli();
        cli.server_url = Some("ws://file/".into());
        cli.secret = Some("s".into());
        let cfg = Config::merge(&cli, FileConfig::default()).unwrap();
        assert_eq!(cfg.hwaccel, None);
        assert_eq!(cfg.hwaccel_device, None);
    }

    #[test]
    fn hwaccel_parsed_from_file() {
        let mut cli = empty_cli();
        cli.server_url = Some("ws://file/".into());
        cli.secret = Some("s".into());
        let file_cfg = FileConfig {
            hwaccel: Some("vaapi".into()),
            hwaccel_device: Some("/dev/dri/renderD128".into()),
            ..Default::default()
        };
        let cfg = Config::merge(&cli, file_cfg).unwrap();
        assert_eq!(cfg.hwaccel, Some(HwAccel::Vaapi));
        assert_eq!(cfg.hwaccel_device.as_deref(), Some("/dev/dri/renderD128"));
    }

    #[test]
    fn hwaccel_cli_overrides_file() {
        let mut cli = empty_cli();
        cli.server_url = Some("ws://file/".into());
        cli.secret = Some("s".into());
        cli.hwaccel = Some("none".into());
        let file_cfg = FileConfig {
            hwaccel: Some("vaapi".into()),
            ..Default::default()
        };
        let cfg = Config::merge(&cli, file_cfg).unwrap();
        assert_eq!(cfg.hwaccel, Some(HwAccel::None));
    }

    #[test]
    fn invalid_hwaccel_errors() {
        let mut cli = empty_cli();
        cli.server_url = Some("ws://file/".into());
        cli.secret = Some("s".into());
        cli.hwaccel = Some("bogus".into());
        let err = Config::merge(&cli, FileConfig::default()).unwrap_err();
        assert!(err.to_string().contains("hwaccel"));
    }
}
