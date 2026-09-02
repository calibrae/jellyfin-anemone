//! Shared library for the `polyp` daemon and the `anemone-mock` test double.
//!
//! See `PROTOCOL.md` at the repo root for the wire protocol these modules implement.

pub mod config;
pub mod hwaccel;
pub mod job;
pub mod mount_local;
pub mod probe;
pub mod protocol;
pub mod ws;
