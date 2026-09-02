//! `mounts[].local` detection, per `PROTOCOL.md` "Protocol v2 additions" -> "Path mapping" and
//! "Placement inputs (v2.1)": is this media tree on storage attached to the agent, so reading a
//! source costs no network round trip?
//!
//! Mirrors `hwaccel.rs`'s split: the classification logic ([`classify_linux`],
//! [`is_local_fs_type`], [`resolve_mount_point`], [`classify_macos_flags`], [`resolve_local`]) is
//! pure and takes injected inputs, so it is unit-testable on any platform. The real syscall/`/proc`
//! reading lives in the platform-gated `detect_local` below.
//!
//! Detection must be real, never guessed from the path string:
//! - **macOS**: `statfs(2)`, testing `f_flags & MNT_LOCAL` -- exactly this question, and correct
//!   for SMB/NFS/AFP mounts.
//! - **Linux**: resolve the mount point that actually backs the path via a longest-prefix match
//!   over `/proc/self/mountinfo` (handles bind mounts and nesting correctly -- looking at the path
//!   string alone does not), then classify its filesystem type.
//!
//! Anything else, or any error along the way, reports `None` (unknown) rather than guessing --
//! the field is optional on the wire and the server treats absent as unknown.

/// One parsed line of `/proc/self/mountinfo`: where it's mounted, and its filesystem type.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct MountInfoEntry {
    pub mount_point: String,
    pub fs_type: String,
}

/// Parse the full contents of `/proc/self/mountinfo` (or an equivalent-shaped fixture). Malformed
/// lines are skipped rather than aborting the whole parse -- one weird line (a kernel we don't
/// fully understand, a truncated read) shouldn't cost us every other mount's classification.
pub fn parse_mountinfo(text: &str) -> Vec<MountInfoEntry> {
    text.lines().filter_map(parse_mountinfo_line).collect()
}

/// Parse one `mountinfo` line. Format (see `proc_pid_mountinfo(5)`):
/// `<id> <parent-id> <maj:min> <root> <mount-point> <options> <optional...> - <fstype> <source> <super-options>`
/// We only need field 5 (mount point, 0-indexed 4) and the field right after the `-` separator
/// (filesystem type) -- the optional-fields block before `-` has a variable length, which is why
/// a fixed column index doesn't work for `fstype` the way it does for `mount point`.
fn parse_mountinfo_line(line: &str) -> Option<MountInfoEntry> {
    let fields: Vec<&str> = line.split_whitespace().collect();
    let sep = fields.iter().position(|&f| f == "-")?;
    let mount_point = fields.get(4)?;
    let fs_type = fields.get(sep + 1)?;
    Some(MountInfoEntry {
        mount_point: unescape_octal(mount_point),
        fs_type: (*fs_type).to_string(),
    })
}

/// `mountinfo` escapes space/tab/newline/backslash in paths as `\NNN` octal byte sequences (e.g.
/// a mount point of `/Volumes/My Passport` appears as `/Volumes/My\040Passport`). Unescape at the
/// byte level (not char-by-char) so multi-byte UTF-8 sequences that got escaped byte-by-byte still
/// round-trip.
fn unescape_octal(s: &str) -> String {
    let bytes = s.as_bytes();
    let mut out: Vec<u8> = Vec::with_capacity(bytes.len());
    let mut i = 0;
    while i < bytes.len() {
        if bytes[i] == b'\\' && i + 3 < bytes.len() {
            let maybe_octal = &s[i + 1..i + 4];
            if maybe_octal.bytes().all(|b| (b'0'..=b'7').contains(&b)) {
                if let Ok(val) = u8::from_str_radix(maybe_octal, 8) {
                    out.push(val);
                    i += 4;
                    continue;
                }
            }
        }
        out.push(bytes[i]);
        i += 1;
    }
    String::from_utf8_lossy(&out).into_owned()
}

/// Find the mount that actually backs `path`: the entry whose `mount_point` is the longest
/// path-segment-boundary prefix of `path`. Handles bind mounts and nesting correctly, unlike
/// matching against `path` itself -- `/mnt/das` and `/mnt/das/data` can be two different mounts
/// (different filesystems, different locality), and `/mnt/dasx` must not match `/mnt/das`.
pub fn resolve_mount_point<'a>(
    path: &str,
    entries: &'a [MountInfoEntry],
) -> Option<&'a MountInfoEntry> {
    entries
        .iter()
        .filter(|e| is_prefix_boundary(&e.mount_point, path))
        .max_by_key(|e| e.mount_point.len())
}

fn is_prefix_boundary(mount_point: &str, path: &str) -> bool {
    if mount_point == "/" {
        return path.starts_with('/');
    }
    if !path.starts_with(mount_point) {
        return false;
    }
    matches!(path.as_bytes().get(mount_point.len()), None | Some(b'/'))
}

/// Linux filesystem types that mean "network, not local": NFS/CIFS/SMB variants, cluster/distributed
/// filesystems, FUSE-backed remote mounts, 9p, WebDAV, etc.
const NETWORK_FS_TYPES: &[&str] = &[
    "nfs",
    "nfs4",
    "cifs",
    "smb3",
    "smbfs",
    "afs",
    "ceph",
    "glusterfs",
    "fuse.sshfs",
    "fuse.rclone",
    "9p",
    "afp",
    "lustre",
    "beegfs",
    "orangefs",
    "davfs",
];

/// Classify a Linux filesystem type as local storage. Unknown types default to local rather than
/// refusing to answer -- ext4/xfs/btrfs/zfs/overlay/etc are the overwhelmingly common case, and a
/// wrong guess here only costs a mount its placement ranking edge, never its eligibility (see
/// `PROTOCOL.md` "Placement inputs (v2.1)": all these fields are advisory).
pub fn is_local_fs_type(fs_type: &str) -> bool {
    !NETWORK_FS_TYPES.contains(&fs_type)
}

/// Pure classifier: given `/proc/self/mountinfo`'s contents and a path, is the mount backing that
/// path local? `None` when no mount entry's prefix matches at all (shouldn't happen against a
/// real mountinfo, which always has `/`, but is a well-formed "unknown" answer for a partial or
/// synthetic input).
pub fn classify_linux(path: &str, mountinfo_text: &str) -> Option<bool> {
    let entries = parse_mountinfo(mountinfo_text);
    let entry = resolve_mount_point(path, &entries)?;
    Some(is_local_fs_type(&entry.fs_type))
}

/// Combine a configured `local` override with a detected value: the override always wins -- an
/// operator may know better than we do (e.g. an iSCSI LUN or a network block device that looks
/// local to `statfs`/`mountinfo`, or vice versa).
pub fn resolve_local(configured: Option<bool>, detected: Option<bool>) -> Option<bool> {
    configured.or(detected)
}

// --- macOS: statfs(2) + MNT_LOCAL ---

/// Decode the `f_flags` word `statfs(2)` returns: `MNT_LOCAL` is exactly the "is this local
/// storage" question, and macOS sets it correctly for SMB/NFS/AFP mounts (unset) vs. local disks
/// (set).
#[cfg(target_os = "macos")]
pub fn classify_macos_flags(f_flags: u32) -> bool {
    (f_flags & (libc::MNT_LOCAL as u32)) != 0
}

/// How long a `statfs(2)` call may block before we call the answer unknown. Mirrors
/// `probe::check_mount`'s timeout and its rationale: a wedged/foreign-session network mount can
/// hang a syscall against it forever, uninterruptibly, and that must degrade to "unknown", never
/// to a hang before the agent ever opens its control connection.
#[cfg(target_os = "macos")]
const STATFS_TIMEOUT: std::time::Duration = std::time::Duration::from_secs(5);

#[cfg(target_os = "macos")]
fn statfs_local(path: &str) -> Option<bool> {
    let c_path = std::ffi::CString::new(path).ok()?;
    let mut buf: std::mem::MaybeUninit<libc::statfs> = std::mem::MaybeUninit::uninit();
    // SAFETY: `c_path` is a valid NUL-terminated string for the duration of this call; `buf` is
    // sized for `libc::statfs` and only read below after a zero return code confirms the kernel
    // filled it in completely.
    let rc = unsafe { libc::statfs(c_path.as_ptr(), buf.as_mut_ptr()) };
    if rc != 0 {
        return None;
    }
    // SAFETY: rc == 0 guarantees the kernel fully populated `buf`.
    let stat = unsafe { buf.assume_init() };
    Some(classify_macos_flags(stat.f_flags))
}

/// Real detection for macOS: `statfs(2)` on a detached thread with a timeout, per the module doc.
#[cfg(target_os = "macos")]
pub fn detect_local(path: &str) -> Option<bool> {
    let (tx, rx) = std::sync::mpsc::channel();
    let probe_path = path.to_string();
    std::thread::spawn(move || {
        let _ = tx.send(statfs_local(&probe_path));
    });
    match rx.recv_timeout(STATFS_TIMEOUT) {
        Ok(result) => result,
        Err(_) => {
            tracing::warn!(
                path,
                timeout_s = STATFS_TIMEOUT.as_secs(),
                "statfs timed out (wedged or foreign-session mount), local-ness unknown"
            );
            None
        }
    }
}

// --- Linux: /proc/self/mountinfo ---

/// Real detection for Linux: read `/proc/self/mountinfo` (a synthetic kernel file -- always fast,
/// never blocks on the mounted filesystem itself, which is exactly why this approach beats a
/// direct `statfs` here) and run it through [`classify_linux`].
#[cfg(target_os = "linux")]
pub fn detect_local(path: &str) -> Option<bool> {
    let text = std::fs::read_to_string("/proc/self/mountinfo").ok()?;
    classify_linux(path, &text)
}

// --- anywhere else: unknown ---

#[cfg(not(any(target_os = "macos", target_os = "linux")))]
pub fn detect_local(path: &str) -> Option<bool> {
    let _ = path;
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    // A shape modeled on abbacchio's real layout (see the live verification in the PR/commit):
    // /mnt/das/data is a local disk mounted under /mnt/das, itself under /mnt; /mnt/offload is an
    // NFS mount from another host; /mnt/bind-media is a bind mount of a subtree of the root fs.
    const SAMPLE_MOUNTINFO: &str = "\
15 20 0:3 / /proc rw,nosuid,nodev,noexec,relatime shared:5 - proc proc rw
16 20 0:14 / /sys rw,nosuid,nodev,noexec,relatime shared:6 - sysfs sysfs rw
20 0 259:2 / / rw,relatime shared:1 - ext4 /dev/nvme0n1p2 rw
21 20 0:16 / /mnt rw,relatime shared:2 - ext4 /dev/sdb1 rw
22 21 0:17 / /mnt/das rw,relatime shared:3 - ext4 /dev/sdc1 rw
23 22 0:18 / /mnt/das/data rw,relatime shared:4 - ext4 /dev/sdd1 rw
24 20 0:19 / /mnt/offload rw,relatime shared:5 - nfs4 10.0.0.5:/export/offload rw
30 20 259:2 /srv/media /mnt/bind-media rw,relatime shared:1 - ext4 /dev/nvme0n1p2 rw
";

    // --- mountinfo parsing ---

    #[test]
    fn parses_all_well_formed_lines() {
        let entries = parse_mountinfo(SAMPLE_MOUNTINFO);
        assert_eq!(entries.len(), 8);
    }

    #[test]
    fn parse_mountinfo_skips_malformed_lines() {
        let text =
            "not a valid mountinfo line at all\n20 0 259:2 / / rw shared:1 - ext4 /dev/x rw\n";
        let entries = parse_mountinfo(text);
        assert_eq!(entries.len(), 1);
        assert_eq!(entries[0].mount_point, "/");
        assert_eq!(entries[0].fs_type, "ext4");
    }

    #[test]
    fn parse_mountinfo_unescapes_octal_spaces() {
        let text =
            "21 20 0:16 / /Volumes/My\\040Passport rw,relatime shared:2 - smbfs //srv/share rw\n";
        let entries = parse_mountinfo(text);
        assert_eq!(entries[0].mount_point, "/Volumes/My Passport");
    }

    // --- longest-prefix mount resolution ---

    #[test]
    fn longest_prefix_resolves_nested_mount_das_data() {
        let entries = parse_mountinfo(SAMPLE_MOUNTINFO);
        let entry = resolve_mount_point("/mnt/das/data/library/movie.mkv", &entries).unwrap();
        assert_eq!(entry.mount_point, "/mnt/das/data");
        assert_eq!(entry.fs_type, "ext4");
    }

    #[test]
    fn longest_prefix_resolves_das_not_data() {
        let entries = parse_mountinfo(SAMPLE_MOUNTINFO);
        let entry = resolve_mount_point("/mnt/das/other-file.txt", &entries).unwrap();
        assert_eq!(entry.mount_point, "/mnt/das");
    }

    #[test]
    fn longest_prefix_resolves_mnt_itself() {
        let entries = parse_mountinfo(SAMPLE_MOUNTINFO);
        let entry = resolve_mount_point("/mnt/something-else.txt", &entries).unwrap();
        assert_eq!(entry.mount_point, "/mnt");
    }

    #[test]
    fn prefix_match_respects_segment_boundary() {
        // "/mnt/dasx" must NOT match the "/mnt/das" mount point -- shared string prefix, but not
        // a path-segment boundary. Falls back to the parent "/mnt" mount.
        let entries = parse_mountinfo(SAMPLE_MOUNTINFO);
        let entry = resolve_mount_point("/mnt/dasx/file", &entries).unwrap();
        assert_eq!(entry.mount_point, "/mnt");
    }

    #[test]
    fn bind_mount_resolves_to_its_own_mount_point() {
        let entries = parse_mountinfo(SAMPLE_MOUNTINFO);
        let entry = resolve_mount_point("/mnt/bind-media/show/e01.mkv", &entries).unwrap();
        assert_eq!(entry.mount_point, "/mnt/bind-media");
        assert_eq!(entry.fs_type, "ext4");
    }

    #[test]
    fn no_matching_mount_is_unknown() {
        let entries = parse_mountinfo("20 0 259:2 / /home rw shared:1 - ext4 /dev/x rw\n");
        assert!(resolve_mount_point("/etc/foo", &entries).is_none());
    }

    // --- filesystem type classification ---

    #[test]
    fn network_fs_types_are_not_local() {
        for fs in [
            "nfs",
            "nfs4",
            "cifs",
            "smb3",
            "smbfs",
            "afs",
            "ceph",
            "glusterfs",
            "fuse.sshfs",
            "fuse.rclone",
            "9p",
            "afp",
            "lustre",
            "beegfs",
            "orangefs",
            "davfs",
        ] {
            assert!(!is_local_fs_type(fs), "{fs} should not be local");
        }
    }

    #[test]
    fn common_local_fs_types_are_local() {
        for fs in ["ext4", "xfs", "btrfs", "zfs", "overlay"] {
            assert!(is_local_fs_type(fs), "{fs} should be local");
        }
    }

    #[test]
    fn unknown_fs_type_defaults_to_local() {
        assert!(is_local_fs_type("zzz-exotic-fs-we-have-never-heard-of"));
    }

    // --- classify_linux end-to-end (pure) ---

    #[test]
    fn classify_linux_local_disk_under_das_data() {
        assert_eq!(
            classify_linux("/mnt/das/data/library/movie.mkv", SAMPLE_MOUNTINFO),
            Some(true)
        );
    }

    #[test]
    fn classify_linux_nfs_offload_is_not_local() {
        assert_eq!(
            classify_linux("/mnt/offload/library/movie.mkv", SAMPLE_MOUNTINFO),
            Some(false)
        );
    }

    #[test]
    fn classify_linux_no_matching_mount_is_unknown() {
        let text = "20 0 259:2 / /home rw shared:1 - ext4 /dev/x rw\n";
        assert_eq!(classify_linux("/etc/foo", text), None);
    }

    // --- local override vs. detection ---

    #[test]
    fn override_wins_over_detection_true() {
        assert_eq!(resolve_local(Some(true), Some(false)), Some(true));
    }

    #[test]
    fn override_wins_over_detection_false() {
        assert_eq!(resolve_local(Some(false), Some(true)), Some(false));
    }

    #[test]
    fn detection_used_when_no_override() {
        assert_eq!(resolve_local(None, Some(true)), Some(true));
        assert_eq!(resolve_local(None, Some(false)), Some(false));
    }

    #[test]
    fn unknown_when_neither_override_nor_detection() {
        assert_eq!(resolve_local(None, None), None);
    }

    // --- macOS: flag decoding + a real statfs call on this machine ---

    #[cfg(target_os = "macos")]
    #[test]
    fn classify_macos_flags_local_bit_set() {
        assert!(classify_macos_flags(libc::MNT_LOCAL as u32));
    }

    #[cfg(target_os = "macos")]
    #[test]
    fn classify_macos_flags_local_bit_combined_with_other_flags() {
        let flags = (libc::MNT_LOCAL as u32) | (libc::MNT_RDONLY as u32);
        assert!(classify_macos_flags(flags));
    }

    #[cfg(target_os = "macos")]
    #[test]
    fn classify_macos_flags_without_local_bit() {
        assert!(!classify_macos_flags(libc::MNT_RDONLY as u32));
    }

    #[cfg(target_os = "macos")]
    #[test]
    fn classify_macos_flags_zero_is_not_local() {
        assert!(!classify_macos_flags(0));
    }

    #[cfg(target_os = "macos")]
    #[test]
    fn detect_local_root_is_local() {
        // "/" is always local disk on real Mac hardware (and in this sandbox).
        assert_eq!(detect_local("/"), Some(true));
    }

    #[cfg(target_os = "macos")]
    #[test]
    fn detect_local_arbitrary_path_has_well_typed_shape() {
        // Not asserting local vs. not -- just that a path we can't assume anything about on an
        // arbitrary dev machine doesn't panic and returns a well-formed Option<bool>.
        let result = detect_local("/some/path/that/might/or/might/not/exist-anemone-probe");
        assert!(matches!(result, None | Some(true) | Some(false)));
    }
}
