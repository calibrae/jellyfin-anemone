//! Hardware-acceleration auto-detection, per `PROTOCOL.md` "Protocol v2 additions" ->
//! "Hardware acceleration".
//!
//! [`resolve`] (and the [`auto_detect`] it falls back to) is the pure decision function: it
//! takes a [`DetectInputs`] snapshot rather than touching the filesystem or spawning processes
//! itself, so the decision logic is unit-testable by injecting inputs. [`probe_render_nodes`]
//! and [`probe_nvidia_present`] are the real-machine probes used at startup to build that
//! snapshot.

use std::process::Stdio;

use crate::protocol::HwAccel;

/// Inputs to the pure detection/decision logic.
#[derive(Debug, Clone, Default)]
pub struct DetectInputs {
    /// `probe::platform_string()`-shaped string, e.g. `"macos-arm64"`, `"linux-x86_64"`.
    pub platform: String,
    /// ffmpeg's reported `-hwaccels` list.
    pub hwaccels: Vec<String>,
    /// DRI render nodes found on this machine, e.g. `["/dev/dri/renderD128"]`; empty if none
    /// (or not applicable, e.g. on macOS). The first entry is used as the default device.
    pub render_nodes: Vec<String>,
    /// Whether an NVIDIA GPU looks present (`/dev/nvidia0` exists, or `nvidia-smi` succeeded).
    pub nvidia_present: bool,
}

/// Resolve the final `(hwaccel, hwaccel_device, reason)` an agent should report, given optional
/// explicit config/CLI overrides and a [`DetectInputs`] snapshot of the machine. `reason` is for
/// startup logging only, not part of the wire protocol.
pub fn resolve(
    explicit_hwaccel: Option<HwAccel>,
    explicit_device: Option<String>,
    inputs: &DetectInputs,
) -> (HwAccel, Option<String>, String) {
    let (hwaccel, auto_device, reason) = match explicit_hwaccel {
        Some(hw) => (
            hw,
            default_device_for(hw, inputs),
            "configured explicitly".to_string(),
        ),
        None => auto_detect(inputs),
    };
    let device = explicit_device.or(auto_device);
    (hwaccel, device, reason)
}

/// The pure auto-detection algorithm, used when `hwaccel` is not configured:
/// - macOS + `videotoolbox` in the probed hwaccels -> videotoolbox.
/// - Linux: a DRI render node exists AND `vaapi` in hwaccels -> vaapi (device = that node);
///   else `cuda` in hwaccels AND an NVIDIA GPU looks present -> nvenc; else `qsv` in hwaccels
///   AND a render node exists -> qsv (device = that node); else none.
/// - anything else -> none.
fn auto_detect(inputs: &DetectInputs) -> (HwAccel, Option<String>, String) {
    let os = inputs
        .platform
        .split('-')
        .next()
        .unwrap_or(&inputs.platform);
    match os {
        "macos" => {
            if inputs.hwaccels.iter().any(|h| h == "videotoolbox") {
                (
                    HwAccel::Videotoolbox,
                    None,
                    "macos + videotoolbox in ffmpeg -hwaccels".to_string(),
                )
            } else {
                (
                    HwAccel::None,
                    None,
                    "macos but videotoolbox not in ffmpeg -hwaccels".to_string(),
                )
            }
        }
        "linux" => {
            let render_node = inputs.render_nodes.first().cloned();
            if let Some(node) = &render_node {
                if inputs.hwaccels.iter().any(|h| h == "vaapi") {
                    return (
                        HwAccel::Vaapi,
                        Some(node.clone()),
                        format!("linux + vaapi in ffmpeg -hwaccels + render node {node}"),
                    );
                }
            }
            if inputs.hwaccels.iter().any(|h| h == "cuda") && inputs.nvidia_present {
                return (
                    HwAccel::Nvenc,
                    None,
                    "linux + cuda in ffmpeg -hwaccels + nvidia device/nvidia-smi present"
                        .to_string(),
                );
            }
            if let Some(node) = &render_node {
                if inputs.hwaccels.iter().any(|h| h == "qsv") {
                    return (
                        HwAccel::Qsv,
                        Some(node.clone()),
                        format!("linux + qsv in ffmpeg -hwaccels + render node {node}"),
                    );
                }
            }
            (
                HwAccel::None,
                None,
                "linux but no matching hwaccel + device combination found".to_string(),
            )
        }
        other => (
            HwAccel::None,
            None,
            format!("no hwaccel auto-detection rule for platform {other}"),
        ),
    }
}

/// The device a given hwaccel profile defaults to when none is configured explicitly: the first
/// discovered DRI render node for vaapi/qsv, nothing otherwise.
fn default_device_for(hwaccel: HwAccel, inputs: &DetectInputs) -> Option<String> {
    match hwaccel {
        HwAccel::Vaapi | HwAccel::Qsv => inputs.render_nodes.first().cloned(),
        _ => None,
    }
}

/// Find this machine's DRI render nodes: `/dev/dri/renderD128` if present, else the
/// lexicographically-first other `/dev/dri/renderD*` device found. Returns an empty `Vec`
/// (never errors) when `/dev/dri` doesn't exist or has no render nodes -- e.g. on macOS, or a
/// Linux box with no GPU.
pub fn probe_render_nodes() -> Vec<String> {
    const DEFAULT_NODE: &str = "/dev/dri/renderD128";
    if std::path::Path::new(DEFAULT_NODE).exists() {
        return vec![DEFAULT_NODE.to_string()];
    }
    let mut nodes: Vec<String> = std::fs::read_dir("/dev/dri")
        .into_iter()
        .flatten()
        .filter_map(|entry| entry.ok())
        .filter_map(|entry| {
            let name = entry.file_name().into_string().ok()?;
            name.starts_with("renderD")
                .then(|| format!("/dev/dri/{name}"))
        })
        .collect();
    nodes.sort();
    nodes
}

/// Whether an NVIDIA GPU looks present: `/dev/nvidia0` exists, or `nvidia-smi` runs
/// successfully. Never errors -- any probe failure just means "not present".
pub fn probe_nvidia_present() -> bool {
    if std::path::Path::new("/dev/nvidia0").exists() {
        return true;
    }
    std::process::Command::new("nvidia-smi")
        .arg("-L")
        .stdout(Stdio::null())
        .stderr(Stdio::null())
        .status()
        .map(|s| s.success())
        .unwrap_or(false)
}

#[cfg(test)]
mod tests {
    use super::*;

    fn inputs(
        platform: &str,
        hwaccels: &[&str],
        render_nodes: &[&str],
        nvidia_present: bool,
    ) -> DetectInputs {
        DetectInputs {
            platform: platform.to_string(),
            hwaccels: hwaccels.iter().map(|s| s.to_string()).collect(),
            render_nodes: render_nodes.iter().map(|s| s.to_string()).collect(),
            nvidia_present,
        }
    }

    #[test]
    fn macos_with_videotoolbox_detected() {
        let i = inputs("macos-arm64", &["videotoolbox"], &[], false);
        let (hw, dev, _) = resolve(None, None, &i);
        assert_eq!(hw, HwAccel::Videotoolbox);
        assert_eq!(dev, None);
    }

    #[test]
    fn macos_without_videotoolbox_falls_back_to_none() {
        let i = inputs("macos-x86_64", &[], &[], false);
        let (hw, dev, _) = resolve(None, None, &i);
        assert_eq!(hw, HwAccel::None);
        assert_eq!(dev, None);
    }

    #[test]
    fn linux_vaapi_with_render_node_detected() {
        let i = inputs("linux-x86_64", &["vaapi"], &["/dev/dri/renderD128"], false);
        let (hw, dev, _) = resolve(None, None, &i);
        assert_eq!(hw, HwAccel::Vaapi);
        assert_eq!(dev.as_deref(), Some("/dev/dri/renderD128"));
    }

    #[test]
    fn linux_vaapi_without_render_node_not_selected() {
        let i = inputs("linux-x86_64", &["vaapi"], &[], false);
        let (hw, dev, _) = resolve(None, None, &i);
        assert_eq!(hw, HwAccel::None);
        assert_eq!(dev, None);
    }

    #[test]
    fn linux_nvenc_with_cuda_and_nvidia_present() {
        let i = inputs("linux-x86_64", &["cuda"], &[], true);
        let (hw, dev, _) = resolve(None, None, &i);
        assert_eq!(hw, HwAccel::Nvenc);
        assert_eq!(dev, None);
    }

    #[test]
    fn linux_cuda_without_nvidia_present_not_selected() {
        let i = inputs("linux-x86_64", &["cuda"], &[], false);
        let (hw, _dev, _) = resolve(None, None, &i);
        assert_eq!(hw, HwAccel::None);
    }

    #[test]
    fn linux_qsv_with_render_node_detected() {
        let i = inputs("linux-x86_64", &["qsv"], &["/dev/dri/renderD129"], false);
        let (hw, dev, _) = resolve(None, None, &i);
        assert_eq!(hw, HwAccel::Qsv);
        assert_eq!(dev.as_deref(), Some("/dev/dri/renderD129"));
    }

    #[test]
    fn linux_prefers_vaapi_over_qsv_when_both_reported() {
        let i = inputs(
            "linux-x86_64",
            &["vaapi", "qsv"],
            &["/dev/dri/renderD128"],
            false,
        );
        let (hw, _dev, _) = resolve(None, None, &i);
        assert_eq!(hw, HwAccel::Vaapi);
    }

    #[test]
    fn linux_no_matching_hwaccel_falls_back_to_none() {
        let i = inputs("linux-aarch64", &[], &[], false);
        let (hw, dev, _) = resolve(None, None, &i);
        assert_eq!(hw, HwAccel::None);
        assert_eq!(dev, None);
    }

    #[test]
    fn explicit_hwaccel_overrides_autodetect() {
        let i = inputs("macos-arm64", &["videotoolbox"], &[], false);
        let (hw, dev, reason) = resolve(Some(HwAccel::None), None, &i);
        assert_eq!(hw, HwAccel::None);
        assert_eq!(dev, None);
        assert_eq!(reason, "configured explicitly");
    }

    #[test]
    fn explicit_hwaccel_vaapi_gets_default_device_from_render_nodes() {
        let i = inputs("linux-x86_64", &["vaapi"], &["/dev/dri/renderD128"], false);
        let (hw, dev, _) = resolve(Some(HwAccel::Vaapi), None, &i);
        assert_eq!(hw, HwAccel::Vaapi);
        assert_eq!(dev.as_deref(), Some("/dev/dri/renderD128"));
    }

    #[test]
    fn explicit_hwaccel_device_overrides_default() {
        let i = inputs("linux-x86_64", &["vaapi"], &["/dev/dri/renderD128"], false);
        let (hw, dev, _) = resolve(
            Some(HwAccel::Vaapi),
            Some("/dev/dri/renderD199".to_string()),
            &i,
        );
        assert_eq!(hw, HwAccel::Vaapi);
        assert_eq!(dev.as_deref(), Some("/dev/dri/renderD199"));
    }

    #[test]
    fn explicit_hwaccel_nvenc_has_no_default_device() {
        let i = inputs("linux-x86_64", &[], &[], false);
        let (hw, dev, _) = resolve(Some(HwAccel::Nvenc), None, &i);
        assert_eq!(hw, HwAccel::Nvenc);
        assert_eq!(dev, None);
    }

    #[test]
    fn unknown_platform_falls_back_to_none() {
        let i = inputs("freebsd-x86_64", &[], &[], false);
        let (hw, dev, _) = resolve(None, None, &i);
        assert_eq!(hw, HwAccel::None);
        assert_eq!(dev, None);
    }
}
