//! Shared native gameplay orchestration over bees-sim and bees-hivemind.
//!
//! The single physics/combat source of truth remains bees-sim. HiveMind
//! strategy IDs, wire ownership and reward reservations remain in
//! bees-hivemind. This crate connects accepted decisions to the real
//! gameplay movement and command timer lifecycle without creating an
//! alternate headless training ruleset.
//!
//! Only source-ported command phases are executable; unported commands
//! fail closed with reservations left for explicit settlement.

#![forbid(unsafe_code)]

pub mod commands;
pub mod squads;
