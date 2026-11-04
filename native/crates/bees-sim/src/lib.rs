//! Shared Bees simulation foundation.
//!
//! Graphical game, headless workers and evaluation must use the same rules.
//! The current code ports selected *actual* movement and projectile contracts;
//! it does not yet implement complete gameplay, physics, observations or RL.
//! No alternate simplified training-only rule implementation is permitted.

#![forbid(unsafe_code)]

mod collider_asset_shape;
pub mod collision;
pub mod combat;
pub mod combat_world;
pub mod control;
pub mod damage;
pub mod geometry;
pub mod movement;
pub mod physics;
pub mod policy;
pub mod projectile;
pub mod projectile_assets;
pub mod projectile_rules;
pub mod simulation;
pub mod ship_assets;
pub mod telemetry;
pub mod timer;
pub mod turret;
