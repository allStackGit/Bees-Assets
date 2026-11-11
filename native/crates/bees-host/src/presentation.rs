//! Zero-copy presentation bridge for the ONE shared native CombatWorld.
//! Rendering is optional and receives borrowed live bodies. Headless PPO
//! never pays to serialize, clone, queue or rebuild presentation state.
//! The renderer owns sprites, cameras and UI; the world owns all collision
//! geometry, health, active projectiles, movement, obstacles and fade timers.

use bees_gameplay::level_events::NativeLevelNotification;
use bees_gameplay::{squads::NativeSquadRoster, player_inputs::NativePlayerAuthority};
use bees_sim::combat_world::CombatWorld;
use bees_sim::simulation::WorldId;

mod software;
mod selection;
pub use software::{NativeCamera, NativeSoftwareRenderer, NativeRenderCounts};
pub use selection::{NativeSquadSelection, NativeSupportMode};

/// This view is valid only between fixed steps, and cannot outlive the
/// NativeHostRuntime's immutable borrow. Presentation event effects are
/// acknowledged separately through NativeLevelNotificationSink, not here.
pub struct NativeRenderWorldView<'a> {
    pub world_id: WorldId,
    pub episode_revision: u64,
    pub tick: u64,
    pub fixed_step_seconds: f32,
    pub combat: &'a CombatWorld,
    pub collision_matrix: &'a bees_sim::physics::CollisionMatrix,
    pub roster: &'a NativeSquadRoster,
    pub player_authority: &'a dyn NativePlayerAuthority,
    pub pending_notifications: &'a [NativeLevelNotification],
}

impl<'a> NativeRenderWorldView<'a> {
    /// Iterate directly over the canonical live bodies. Projectiles,
    /// asteroids and ships all retain their authored collider/gameplay
    /// types; no display-only positional cache or fake interpolator.
    pub fn has_live_ships(&self) -> bool {
        self.combat.render_ships().next().is_some()
    }

    pub fn live_ship_count(&self) -> usize {
        self.combat.total_living_ships()
    }
}
