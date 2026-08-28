//! Shared library for the `jfc-agent` daemon and the `jfc-mock-server` test double.
//!
//! See `PROTOCOL.md` at the repo root for the wire protocol these modules implement.

pub mod config;
pub mod job;
pub mod probe;
pub mod protocol;
pub mod ws;
