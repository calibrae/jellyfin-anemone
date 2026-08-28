//! Startup capability probe: run `<ffmpeg> -version -hwaccels -encoders -decoders -filters` and
//! parse the results, plus mount readability checks. See the deliverables doc for the exact
//! ffmpeg list formats these parsers target (`-encoders`/`-decoders` share one flags-column
//! format with `-filters`; `-hwaccels` is a bare name-per-line list).

use anyhow::{Context, Result};

use crate::protocol::{FfmpegCaps, MountStatus};

/// Run the full capability probe against the ffmpeg binary at `ffmpeg_path`.
pub async fn probe_ffmpeg(ffmpeg_path: &str) -> Result<FfmpegCaps> {
    let version_out = run_capture(ffmpeg_path, &["-version"]).await?;
    let hwaccels_out = run_capture(ffmpeg_path, &["-hwaccels"]).await?;
    let encoders_out = run_capture(ffmpeg_path, &["-encoders"]).await?;
    let decoders_out = run_capture(ffmpeg_path, &["-decoders"]).await?;
    let filters_out = run_capture(ffmpeg_path, &["-filters"]).await?;

    let version = parse_version(&version_out)
        .with_context(|| format!("could not parse `ffmpeg -version` output from {ffmpeg_path}"))?;

    Ok(FfmpegCaps {
        path: ffmpeg_path.to_string(),
        version,
        hwaccels: parse_hwaccels(&hwaccels_out),
        encoders: parse_flagged_list(&encoders_out, "Encoders:"),
        decoders: parse_flagged_list(&decoders_out, "Decoders:"),
        filters: parse_flagged_list(&filters_out, "Filters:"),
    })
}

/// Run `<path> <args>` and capture stdout+stderr concatenated (ffmpeg splits its version/config
/// banner from the actual list across the two streams depending on which `-x` flag is used, so
/// callers that just want the printed list should look at both).
async fn run_capture(path: &str, args: &[&str]) -> Result<String> {
    let output = tokio::process::Command::new(path)
        .args(args)
        .output()
        .await
        .with_context(|| format!("failed to spawn `{path} {}`", args.join(" ")))?;
    let mut combined = String::from_utf8_lossy(&output.stdout).into_owned();
    combined.push('\n');
    combined.push_str(&String::from_utf8_lossy(&output.stderr));
    Ok(combined)
}

/// Parse the version token out of ffmpeg's banner first line, e.g.
/// `ffmpeg version 7.1.2-Jellyfin Copyright (c) 2000-2025 the FFmpeg developers` -> `7.1.2-Jellyfin`.
pub fn parse_version(text: &str) -> Option<String> {
    let first_line = text.lines().next()?;
    let mut tokens = first_line.split_whitespace();
    if tokens.next()? != "ffmpeg" {
        return None;
    }
    if tokens.next()? != "version" {
        return None;
    }
    tokens.next().map(|s| s.to_string())
}

/// Parse `-hwaccels` output: a header line, then one hwaccel name per line until a blank line.
pub fn parse_hwaccels(text: &str) -> Vec<String> {
    let mut out = Vec::new();
    let mut in_list = false;
    for line in text.lines() {
        let trimmed = line.trim();
        if trimmed == "Hardware acceleration methods:" {
            in_list = true;
            continue;
        }
        if !in_list {
            continue;
        }
        if trimmed.is_empty() {
            break;
        }
        out.push(trimmed.to_string());
    }
    out
}

/// Parse `-encoders`/`-decoders`/`-filters` output. All three share one format after the header:
/// ` <flags> <name>   <description...>` (filters have an extra I/O-arity column before the
/// description; encoders/decoders don't — irrelevant to us since we only take the name token).
/// Legend lines look like ` V..... = Video` (second token is a literal `=`) and are skipped, as
/// is the `------` separator ffmpeg prints under the legend.
pub fn parse_flagged_list(text: &str, header: &str) -> Vec<String> {
    let mut out = Vec::new();
    let mut in_list = false;
    for line in text.lines() {
        let trimmed = line.trim();
        if trimmed == header {
            in_list = true;
            continue;
        }
        if !in_list || trimmed.is_empty() {
            continue;
        }
        if trimmed.chars().all(|c| c == '-') {
            continue; // "------" separator
        }
        let mut tokens = trimmed.split_whitespace();
        let Some(_flags) = tokens.next() else {
            continue;
        };
        let Some(name) = tokens.next() else {
            continue;
        };
        if name == "=" {
            continue; // legend line, e.g. " V..... = Video"
        }
        out.push(name.to_string());
    }
    out
}

/// Build this agent's platform string: `macos-arm64`, `macos-x86_64`, `linux-x86_64`,
/// `linux-aarch64`, ...
pub fn platform_string() -> String {
    let os = if cfg!(target_os = "macos") {
        "macos"
    } else if cfg!(target_os = "linux") {
        "linux"
    } else {
        std::env::consts::OS
    };
    let arch = if cfg!(target_arch = "aarch64") {
        "arm64"
    } else if cfg!(target_arch = "x86_64") {
        "x86_64"
    } else {
        std::env::consts::ARCH
    };
    format!("{os}-{arch}")
}

/// Check a configured mount: exists, is a directory, is readable, and is non-empty. Never
/// errors — a bad mount is reported via `ok: false`, not a startup failure.
pub fn check_mount(path: &str) -> MountStatus {
    MountStatus {
        path: path.to_string(),
        ok: mount_ok(path),
    }
}

fn mount_ok(path: &str) -> bool {
    let p = std::path::Path::new(path);
    let meta = match std::fs::metadata(p) {
        Ok(m) => m,
        Err(_) => return false,
    };
    if !meta.is_dir() {
        return false;
    }
    match std::fs::read_dir(p) {
        Ok(mut entries) => entries.next().is_some(),
        Err(_) => false,
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    macro_rules! fixture {
        ($name:expr) => {
            include_str!(concat!(
                env!("CARGO_MANIFEST_DIR"),
                "/tests/fixtures/",
                $name
            ))
        };
    }

    #[test]
    fn parses_jellyfin_ffmpeg_version() {
        let text = fixture!("ffmpeg-version-jellyfin.txt");
        assert_eq!(parse_version(text).as_deref(), Some("7.1.2-Jellyfin"));
    }

    #[test]
    fn parses_homebrew_ffmpeg_version() {
        let text = fixture!("ffmpeg-version-homebrew.txt");
        assert_eq!(parse_version(text).as_deref(), Some("8.0.1"));
    }

    #[test]
    fn parse_version_rejects_garbage() {
        assert_eq!(parse_version("not ffmpeg output at all"), None);
        assert_eq!(parse_version(""), None);
    }

    #[test]
    fn parses_jellyfin_hwaccels() {
        let text = fixture!("ffmpeg-hwaccels-jellyfin.txt");
        let hwaccels = parse_hwaccels(text);
        assert!(hwaccels.contains(&"videotoolbox".to_string()));
        // must not have picked up banner/config lines
        assert!(!hwaccels.iter().any(|h| h.contains("ffmpeg version")));
    }

    #[test]
    fn parses_homebrew_hwaccels() {
        let text = fixture!("ffmpeg-hwaccels-homebrew.txt");
        let hwaccels = parse_hwaccels(text);
        assert!(hwaccels.contains(&"videotoolbox".to_string()));
    }

    #[test]
    fn parses_jellyfin_encoders() {
        let text = fixture!("ffmpeg-encoders-jellyfin.txt");
        let encoders = parse_flagged_list(text, "Encoders:");
        assert!(encoders.contains(&"h264_videotoolbox".to_string()));
        assert!(encoders.contains(&"hevc_videotoolbox".to_string()));
        assert!(encoders.contains(&"libx264".to_string()));
        assert!(
            encoders.contains(&"aac".to_string()) || encoders.iter().any(|e| e.contains("aac"))
        );
        // legend / separator lines must not leak through
        assert!(!encoders
            .iter()
            .any(|e| e == "=" || e.chars().all(|c| c == '-')));
        assert!(!encoders.contains(&"Video".to_string()));
    }

    #[test]
    fn parses_jellyfin_decoders() {
        let text = fixture!("ffmpeg-decoders-jellyfin.txt");
        let decoders = parse_flagged_list(text, "Decoders:");
        assert!(decoders.contains(&"h264".to_string()));
        assert!(decoders.contains(&"hevc".to_string()));
        assert!(!decoders.iter().any(|e| e == "="));
    }

    #[test]
    fn parses_jellyfin_filters() {
        let text = fixture!("ffmpeg-filters-jellyfin.txt");
        let filters = parse_flagged_list(text, "Filters:");
        assert!(filters.contains(&"scale_vt".to_string()));
        assert!(filters.contains(&"scale".to_string()));
        assert!(filters.contains(&"overlay".to_string()));
        assert!(!filters.iter().any(|f| f == "="));
        // the legend uses single-letter flags like "A = Audio input/output" -- must not appear
        assert!(!filters.contains(&"Audio".to_string()));
    }

    #[test]
    fn parses_homebrew_encoders_decoders_filters_without_panicking() {
        let encoders = parse_flagged_list(fixture!("ffmpeg-encoders-homebrew.txt"), "Encoders:");
        let decoders = parse_flagged_list(fixture!("ffmpeg-decoders-homebrew.txt"), "Decoders:");
        let filters = parse_flagged_list(fixture!("ffmpeg-filters-homebrew.txt"), "Filters:");
        assert!(encoders.contains(&"libx264".to_string()));
        assert!(decoders.contains(&"h264".to_string()));
        assert!(filters.contains(&"scale".to_string()));
    }

    #[test]
    fn platform_string_has_expected_shape() {
        let p = platform_string();
        let (os, arch) = p.split_once('-').expect("platform string has a dash");
        assert!(matches!(os, "macos" | "linux"), "unexpected os: {os}");
        assert!(
            matches!(arch, "arm64" | "x86_64"),
            "unexpected arch: {arch}"
        );
    }

    #[test]
    fn check_mount_ok_for_nonempty_dir() {
        let dir = tempdir();
        std::fs::write(dir.join("file.txt"), b"hi").unwrap();
        let status = check_mount(dir.to_str().unwrap());
        assert!(status.ok);
        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn check_mount_fails_for_empty_dir() {
        let dir = tempdir();
        let status = check_mount(dir.to_str().unwrap());
        assert!(!status.ok);
        std::fs::remove_dir_all(&dir).ok();
    }

    #[test]
    fn check_mount_fails_for_missing_path() {
        let status = check_mount("/nonexistent/definitely/not/here/anemone-test");
        assert!(!status.ok);
    }

    #[test]
    fn check_mount_fails_for_file_not_dir() {
        let dir = tempdir();
        let file = dir.join("notadir");
        std::fs::write(&file, b"hi").unwrap();
        let status = check_mount(file.to_str().unwrap());
        assert!(!status.ok);
        std::fs::remove_dir_all(&dir).ok();
    }

    fn tempdir() -> std::path::PathBuf {
        let mut p = std::env::temp_dir();
        let unique = format!(
            "polyp-test-{}-{}",
            std::process::id(),
            std::time::SystemTime::now()
                .duration_since(std::time::UNIX_EPOCH)
                .unwrap()
                .as_nanos()
        );
        p.push(unique);
        std::fs::create_dir_all(&p).unwrap();
        p
    }

    #[tokio::test]
    async fn probe_ffmpeg_end_to_end_if_available() {
        let candidates = [
            "/opt/homebrew/bin/ffmpeg",
            "/Applications/Jellyfin.app/Contents/MacOS/ffmpeg",
        ];
        let Some(path) = candidates.iter().find(|p| std::path::Path::new(p).exists()) else {
            eprintln!("skipping: no local ffmpeg binary found");
            return;
        };
        let caps = probe_ffmpeg(path).await.expect("probe should succeed");
        assert!(!caps.version.is_empty());
        assert!(!caps.encoders.is_empty());
        assert!(!caps.decoders.is_empty());
        assert!(!caps.filters.is_empty());
    }
}
