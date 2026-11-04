//! Shared explosion lifecycle driven by authored Unity AnimationClip events.
//!
//! Source clips:
//!   Sprites/Projectiles/Animations/Frigate Rocket Explosion.anim
//!   Sprites/Projectiles/Animations/Fireship Explosion.anim
//! Imported into native/assets/projectile-colliders.json. These gameplay
//! events are not rendered animation frames: changing the collision radius,
//! disabling damage, and ending a projectile affect both graphics and RL.
//!
//! Each projectile keeps only a clock and an index into immutable shared
//! events. No event lists are allocated during stepping or on each spawn.
//! The caller uses the same simulation clock as other gameplay systems;
//! Unity Animator.Update / FixedUpdate ordering still requires parity checks.

use std::sync::Arc;

#[derive(Clone, Copy, Debug, PartialEq)]
pub enum ExplosionAction {
    SetRadius(f32),
    SetHarmless,
    Kill,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct ExplosionEvent {
    pub time_seconds: f32,
    pub action: ExplosionAction,
}

/// Immutable animation events are shared between all explosions of a type.
pub struct ExplosionTimeline {
    events: Arc<[ExplosionEvent]>,
    elapsed_seconds: f32,
    next_event: usize,
}

impl ExplosionTimeline {
    pub fn new(events: Arc<[ExplosionEvent]>) -> Self {
        Self {
            events,
            elapsed_seconds: 0.0,
            next_event: 0,
        }
    }

    pub fn advance(&mut self, dt: f32) {
        self.elapsed_seconds += dt;
    }

    /// Return one event at a time. The world applies each effect and may
    /// immediately end the projectile on Kill without an intermediate Vec.
    pub fn take_due(&mut self) -> Option<ExplosionAction> {
        let event = *self.events.get(self.next_event)?;
        if event.time_seconds > self.elapsed_seconds {
            return None;
        }
        self.next_event += 1;
        Some(event.action)
    }

    pub fn elapsed_seconds(&self) -> f32 {
        self.elapsed_seconds
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::projectile_assets::ProjectileAssetCatalog;

    #[test]
    fn frigate_explosion_harmless_and_kill_events() {
        let catalog = ProjectileAssetCatalog::embedded().unwrap();
        let mut clock = ExplosionTimeline::new(
            catalog.by_type(13).unwrap().explosion_events.clone()
        );
        clock.advance(0.49);
        assert_eq!(clock.take_due(), None);
        clock.advance(0.01);
        assert_eq!(clock.take_due(), Some(ExplosionAction::SetHarmless));
        clock.advance(0.3);
        assert_eq!(clock.take_due(), Some(ExplosionAction::Kill));
        assert_eq!(clock.take_due(), None);
    }

    #[test]
    fn fireship_radius_events_are_applied_in_authored_order() {
        let catalog = ProjectileAssetCatalog::embedded().unwrap();
        let mut clock = ExplosionTimeline::new(
            catalog.by_type(14).unwrap().explosion_events.clone()
        );
        clock.advance(2.9);
        let mut events = Vec::new();
        while let Some(event) = clock.take_due() {
            events.push(event);
        }
        assert_eq!(events, [
            ExplosionAction::SetRadius(1.0),
            ExplosionAction::SetRadius(17.0),
            ExplosionAction::SetRadius(34.0),
            ExplosionAction::SetRadius(51.0),
            ExplosionAction::SetRadius(56.0),
            ExplosionAction::SetHarmless,
            ExplosionAction::Kill,
        ]);
    }
}
