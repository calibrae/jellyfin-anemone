//! Config: a TOML file (`--config`, default `/etc/jfc-agent.toml`) with CLI overrides taking
//! precedence over file values, which take precedence over built-in defaults.

use std::path::{Path, PathBuf};

use anyhow::{Context, Result};
use clap::Parser;
use serde::Deserialize;

#[derive(Parser, Debug)]
#[command(
    name = "jfc-agent",
    about = "Runs ffmpeg transcodes on behalf of a Jellyfin cluster plugin"
)]
pub struct Cli {
    /// Path to the TOML config file.
    #[arg(long, default_value = "/etc/jfc-agent.toml")]
    pub config: PathBuf,

    /// Control WebSocket URL, e.g. ws://10.240.0.1:8096/Cluster/agents/ws
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

    /// Mount paths that must be readable (comma-separated, or repeat the flag).
    #[arg(long, value_delimiter = ',')]
    pub mounts: Option<Vec<String>>,

    /// Log level: trace, debug, info, warn, error. Also honors RUST_LOG.
    #[arg(long)]
    pub log_level: Option<String>,
}

#[derive(Debug, Default, Deserialize)]
struct FileConfig {
    server_url: Option<String>,
    secret: Option<String>,
    name: Option<String>,
    ffmpeg: Option<String>,
    max_sessions: Option<u32>,
    mounts: Option<Vec<String>>,
    log_level: Option<String>,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct Config {
    pub server_url: String,
    pub secret: String,
    pub name: String,
    pub ffmpeg: String,
    pub max_sessions: u32,
    pub mounts: Vec<String>,
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
        let mounts = cli.mounts.clone().or(file_cfg.mounts).unwrap_or_default();
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
        .unwrap_or_else(|| "jfc-agent".to_string())
}

#[cfg(test)]
mod tests {
    use super::*;

    fn empty_cli() -> Cli {
        Cli {
            config: PathBuf::from("/nonexistent/jfc-agent.toml"),
            server_url: None,
            secret: None,
            name: None,
            ffmpeg: None,
            max_sessions: None,
            mounts: None,
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
            server_url = "ws://10.240.0.1:8096/Cluster/agents/ws"
            secret = "sekrit"
            name = "trish"
            ffmpeg = "/opt/jfc/ffmpeg"
            max_sessions = 3
            mounts = ["/Volumes/data"]
            log_level = "info"
        "#;
        let file_cfg: FileConfig = toml::from_str(toml_text).unwrap();
        assert_eq!(
            file_cfg.server_url.as_deref(),
            Some("ws://10.240.0.1:8096/Cluster/agents/ws")
        );
        assert_eq!(
            file_cfg.mounts.as_deref(),
            Some(&["/Volumes/data".to_string()][..])
        );
    }
}
