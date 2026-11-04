//! Shared Bees simulation foundation.
//!
//! The graphical game, headless workers and evaluators must call the same
//! simulation rules. This crate does not claim gameplay parity yet. No
//! competing simplified training-only rules are permitted.

#![forbid(unsafe_code)]

pub mod geometry;
pub mod policy;
pub mod simulation;
pub mod telemetry;
