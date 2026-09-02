//! `status.load` sampling, per `PROTOCOL.md` "Placement inputs (v2.1)": the agent's own view of
//! how busy it is, 0..1, sent with every `status` frame (every ~10s -- cheap by construction).
//!
//! [`normalize`] is the pure decision function -- unit-testable without touching the host --
//! separated from the real `/proc`/libc reading in [`sample`], mirroring `hwaccel.rs`'s split.

/// Normalize a 1-minute load average by CPU count into 0..1: no load -> 0.0, load equal to CPU
/// count -> 1.0 (fully committed), anything above clamps to 1.0 (the server only needs "how
/// saturated", not an unbounded overcommit figure). `ncpu` of 0 is treated as 1 to avoid a
/// divide-by-zero -- shouldn't happen in practice (`std::thread::available_parallelism()` returns
/// a `NonZeroUsize`), but a load reading of exactly 0 must not become a divide-by-zero either.
pub fn normalize(load1: f64, ncpu: usize) -> f64 {
    if !load1.is_finite() || load1 <= 0.0 {
        return 0.0;
    }
    let ncpu = ncpu.max(1) as f64;
    (load1 / ncpu).min(1.0)
}

/// Sample the host's 1-minute load average and normalize it by CPU count. `None` when it can't be
/// read on this platform (or the read fails) -- the caller keeps sending `load: None`, never a
/// guess.
pub fn sample() -> Option<f64> {
    let load1 = raw_load1()?;
    let ncpu = std::thread::available_parallelism()
        .map(|n| n.get())
        .unwrap_or(1);
    Some(normalize(load1, ncpu))
}

/// Parse the first (1-minute) field of `/proc/loadavg`, e.g. `"0.52 0.58 0.59 1/234 5678\n"` ->
/// `0.52`. Pure and testable independent of actually reading the file.
pub fn parse_proc_loadavg(text: &str) -> Option<f64> {
    text.split_whitespace().next()?.parse::<f64>().ok()
}

#[cfg(target_os = "linux")]
fn raw_load1() -> Option<f64> {
    let text = std::fs::read_to_string("/proc/loadavg").ok()?;
    parse_proc_loadavg(&text)
}

#[cfg(target_os = "macos")]
fn raw_load1() -> Option<f64> {
    let mut loadavg: [libc::c_double; 3] = [0.0; 3];
    // SAFETY: `loadavg` has room for exactly the 3 samples we tell `getloadavg` it may write.
    let n = unsafe { libc::getloadavg(loadavg.as_mut_ptr(), loadavg.len() as libc::c_int) };
    if n <= 0 {
        return None;
    }
    Some(loadavg[0])
}

#[cfg(not(any(target_os = "linux", target_os = "macos")))]
fn raw_load1() -> Option<f64> {
    None
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn zero_load_normalizes_to_zero() {
        assert_eq!(normalize(0.0, 8), 0.0);
    }

    #[test]
    fn load_equal_to_ncpu_normalizes_to_one() {
        assert_eq!(normalize(4.0, 4), 1.0);
    }

    #[test]
    fn load_above_ncpu_clamps_to_one() {
        assert_eq!(normalize(12.0, 4), 1.0);
    }

    #[test]
    fn load_below_ncpu_is_proportional() {
        assert_eq!(normalize(2.0, 4), 0.5);
    }

    #[test]
    fn negative_load_normalizes_to_zero() {
        // Shouldn't happen for a real load average, but must not go negative or panic.
        assert_eq!(normalize(-1.0, 4), 0.0);
    }

    #[test]
    fn non_finite_load_normalizes_to_zero() {
        assert_eq!(normalize(f64::NAN, 4), 0.0);
        assert_eq!(normalize(f64::INFINITY, 4), 0.0);
    }

    #[test]
    fn zero_ncpu_does_not_divide_by_zero() {
        assert_eq!(normalize(1.0, 0), 1.0);
    }

    #[test]
    fn parses_proc_loadavg_first_field() {
        assert_eq!(
            parse_proc_loadavg("0.52 0.58 0.59 1/234 5678\n"),
            Some(0.52)
        );
    }

    #[test]
    fn parse_proc_loadavg_rejects_garbage() {
        assert_eq!(parse_proc_loadavg(""), None);
        assert_eq!(parse_proc_loadavg("not a number here"), None);
    }

    #[test]
    fn sample_returns_a_clamped_value_on_supported_platforms() {
        // No specific value to assert -- just that on Linux/macOS this actually reads something
        // and it's within the documented 0..1 range; on any other platform it's None.
        if let Some(v) = sample() {
            assert!((0.0..=1.0).contains(&v), "sample() out of range: {v}");
        }
    }
}
