//! Optional physical-world signature for source-validated native replay.
//!
//! This deliberately hashes *current canonical physical state*, not the
//! internal random stream, command reservations, model weights or pending
//! training rewards. Those have separate owners. A mismatch proves physical
//! replay divergence; a match does NOT prove complete behavioral parity.
//! Only explicit trace capture/playback calls this; headless PPO hot loops
//! never pay per-step hashing, serialization, or entity sorting.

use bees_sim::combat_world::CombatWorld;
use bees_sim::geometry::Vec2;
use bees_sim::simulation::WorldId;
use sha2::{Digest, Sha256};

#[inline]
fn number(hash: &mut Sha256, value: i64) {
    hash.update(value.to_le_bytes());
}

#[inline]
fn scalar(hash: &mut Sha256, value: f32) {
    hash.update(value.to_bits().to_le_bytes());
}

#[inline]
fn point(hash: &mut Sha256, value: Vec2) {
    scalar(hash, value.x);
    scalar(hash, value.y);
}

/// Source array order is the physical simulation's existing deterministic
/// registration order. No BTreeMap or entity snapshot is needed.
pub fn physical_world_sha256(
    id: WorldId, episode_revision: u64, tick: u64,
    combat: &CombatWorld,
) -> String {
    let mut hash = Sha256::new();
    hash.update(b"bees-native-physical-state-v1");
    hash.update(id.0.to_le_bytes());
    hash.update(episode_revision.to_le_bytes());
    hash.update(tick.to_le_bytes());

    for (ship, body) in combat.render_ships() {
        hash.update(b"S");
        number(&mut hash, ship.entity_id);
        hash.update(ship.side.to_le_bytes());
        hash.update([ship.ship_type]);
        hash.update(ship.vitals.health().to_le_bytes());
        hash.update(ship.vitals.max_health().to_le_bytes());
        hash.update(ship.vitals.tsv().to_le_bytes());
        hash.update(ship.vitals.mined_cargo().to_le_bytes());
        point(&mut hash, body.motion.position);
        scalar(&mut hash, body.motion.rotation_degrees);
        point(&mut hash, body.motion.body_velocity);
        scalar(&mut hash, body.motion.current_speed);
        hash.update([u8::from(body.motion.has_target_coordinates)]);
        point(&mut hash, body.motion.target_coordinates);
        hash.update([u8::from(body.motion.is_moving)]);
    }

    for (shot, body) in combat.render_projectiles() {
        hash.update(b"P");
        number(&mut hash, shot.entity_id);
        hash.update([shot.projectile_type]);
        number(&mut hash, shot.owner.shooter_id);
        hash.update(shot.power.to_le_bytes());
        number(&mut hash, body.motion.attribution.root_shot_id);
        number(&mut hash, body.motion.attribution.command_outcome_id);
        point(&mut hash, body.motion.position);
        point(&mut hash, body.motion.velocity);
        scalar(&mut hash, body.motion.angle_radians);
        scalar(&mut hash, body.rotation_degrees);
    }

    for (obstacle, body) in combat.render_obstacles() {
        hash.update(b"O");
        number(&mut hash, obstacle.entity_id);
        hash.update([obstacle.kind as u8]);
        point(&mut hash, body.pose.position);
        scalar(&mut hash, body.pose.rotation_degrees);
        point(&mut hash, body.linear_velocity);
        scalar(&mut hash, body.angular_velocity_degrees);
        if let Some(vitals) = obstacle.vitals {
            hash.update(b"V");
            hash.update(vitals.health.to_le_bytes());
            hash.update([u8::from(vitals.is_dead)]);
        }
        if let Some(object) = combat.map_object(obstacle.entity_id) {
            hash.update(b"M");
            hash.update(object.health.to_le_bytes());
        }
    }

    for piece in combat.render_asteroid_pieces() {
        hash.update(b"F");
        number(&mut hash, piece.entity_id);
        point(&mut hash, piece.pose.position);
        scalar(&mut hash, piece.pose.rotation_degrees);
        point(&mut hash, piece.velocity);
        hash.update([piece.half_seconds]);
    }

    format!("{:x}", hash.finalize())
}
