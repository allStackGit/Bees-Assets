//! Shared Bees simulation foundation.
//!
//! Graphical game, headless workers and evaluation must use the same rules.
//! The current code ports selected *actual* movement and projectile contracts;
//! it does not yet implement complete gameplay, physics, observations or RL.
//! No alternate simplified training-only rule implementation is permitted.

#![forbid(unsafe_code)]

pub mod asteroid_assets;
pub mod asteroid_fragments;
pub mod bomb;
pub mod box_obstacle_assets;
mod collider_asset_shape;
pub mod collision;
pub mod combat;
pub mod combat_world;
pub mod control;
pub mod damage;
pub mod damage_reservations;
pub mod explosion_lifecycle;
pub mod episode_ship_identity;
pub mod geometry;
pub mod movement;
pub mod local_navigation_observation;
pub mod navigation;
pub mod navigation_clearance;
pub mod navigation_search;
pub mod navigation_requests;
pub mod navigation_movement;
pub mod navigation_runtime;
pub mod navigation_scheduler;
pub mod obstacle;
pub mod physics;
pub mod perception;
pub mod perception_entities;
pub mod perception_environment;
pub mod perception_weapons;
pub mod policy;
pub mod policy_batch;
pub mod polygon_obstacle_assets;
pub mod projectile;
pub mod projectile_assets;
pub mod projectile_rules;
pub mod rl_shaping;
pub mod simulation;
pub mod ship_assets;
pub mod squad_movement;
pub mod team_exploration;
pub mod telemetry;
pub mod timer;
pub mod turret;
