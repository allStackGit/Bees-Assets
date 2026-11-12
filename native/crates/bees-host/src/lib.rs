//! Operating-system adapters used by both native game and headless workers.
//! The simulation, command lifecycle and protocol remain in their pure crates.

#![forbid(unsafe_code)]

pub mod checkpoint_file;
pub mod status;
pub mod durable_connection;
pub mod journal_recovery;
pub mod runtime;
pub mod source_command_scheduler;

pub mod policy_process;

pub mod socket;
pub mod socket_worker;
pub mod scene_assets;
pub mod scene_builder;
mod reinforcements;
mod campaign_staging;
pub use reinforcements::NativeReinforcementPlan;
pub mod ship_specials;
pub mod fleet_inventory;
pub mod policy_only_metadata;
pub mod presentation;
pub mod player_trace;
pub mod policy_action_trace;
#[cfg(feature = "client")]
pub mod client;
pub mod unity_scene_snapshot;

pub mod notification_file;
mod policy_capture;
