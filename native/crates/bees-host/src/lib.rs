//! Operating-system adapters used by both native game and headless workers.
//! The simulation, command lifecycle and protocol remain in their pure crates.

#![forbid(unsafe_code)]

pub mod checkpoint_file;
