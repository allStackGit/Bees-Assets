//! Wire-compatible Hive Mind client/server contract for the native Bees game.
//!
//! The historical Node backend remains authoritative for authenticated
//! connections, shared Hive Mind Games, learned strategy selection,
//! persistent OutcomeId reservations and durable MySQL commits. This crate
//! makes request, selection, squad lifecycle and outcome semantics explicit
//! without pulling network, database or game-rendering dependencies into
//! the single shared bees-sim physics/learning engine.
//!
//! A native Hive Mind server implementation may use these exact contracts,
//! but must preserve the existing Unity client's protocol and persistence
//! semantics unless a coordinated protocol migration is undertaken.

#![forbid(unsafe_code)]

pub mod connection;
pub mod dispatch;
pub mod outcomes;
pub mod protocol;
pub mod session;
pub mod wire;
