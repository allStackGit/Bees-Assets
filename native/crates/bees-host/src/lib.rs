//! Operating-system adapters used by both native game and headless workers.
//! The simulation, command lifecycle and protocol remain in their pure crates.

#![forbid(unsafe_code)]

pub mod checkpoint_file;
pub mod status;
pub mod durable_connection;
pub mod runtime;

pub mod policy_process;

pub mod socket;
pub mod scene_assets;
