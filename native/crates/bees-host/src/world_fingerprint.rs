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
use bees_gameplay::commands::NativeSquadCommandDirector;
use bees_gameplay::player_inputs::NativePlayerAuthority;
use bees_gameplay::squads::NativeSquadRoster;
use bees_hivemind::session::SquadLifetime;
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


/// A second, trace-only signature: authoritative squads, live command
/// selections, local player settings, and learned faction sight. This is
/// deliberately NOT a hash of network queues, RNG, hidden command timers,
/// model weights or pending reward settlements.
fn owner(hash: &mut Sha256, owner: SquadLifetime) {
    number(hash, owner.level_id);
    number(hash, owner.squad_id);
    hash.update(owner.lifecycle_id.to_le_bytes());
}

fn maybe_owner(hash: &mut Sha256, value: Option<SquadLifetime>) {
    if let Some(value) = value {
        hash.update([1_u8]);
        owner(hash, value);
    } else { hash.update([0_u8]); }
}

fn sorted_ids(hash: &mut Sha256, ids: Option<&std::collections::HashSet<i64>>) {
    let mut sorted = ids.map_or_else(Vec::new,
        |ids| ids.iter().copied().collect::<Vec<_>>());
    sorted.sort_unstable();
    hash.update((sorted.len() as u64).to_le_bytes());
    for id in sorted { number(hash, id); }
}

/// Stable across HashMap layout, host OS and processing architecture.
/// Called at trace checkpoints, never in the simulation fixed-step hot path.
pub fn gameplay_world_sha256(
    id: WorldId, revision: u64, tick: u64,
    combat: &CombatWorld,
    roster: &NativeSquadRoster,
    commands: &NativeSquadCommandDirector,
    player: &dyn NativePlayerAuthority,
) -> String {
    let mut hash = Sha256::new();
    hash.update(b"bees-native-gameplay-state-v1");
    hash.update(id.0.to_le_bytes());
    hash.update(revision.to_le_bytes());
    hash.update(tick.to_le_bytes());
    hash.update([u8::from(player.player_fog_of_war_enabled())]);
    if let Some(side) = player.player_viewer_side() {
        hash.update([1_u8]);
        hash.update(side.to_le_bytes());
    } else { hash.update([0_u8]); }

    // Live source order and exact member ownership, not pending waves
    // or a UI-only roster. A pooled numeric squad ID is not its lifetime.
    for &key in roster.source_order() {
        let Some(squad) = roster.get(key) else { continue };
        hash.update(b"S");
        owner(&mut hash, key);
        hash.update(squad.side.to_le_bytes());
        hash.update((squad.members.len() as u64).to_le_bytes());
        for &ship_id in &squad.members { number(&mut hash, ship_id); }
        maybe_owner(&mut hash, squad.mother_squad);
        if let Some(kind) = commands.current_kind(key) {
            hash.update([1_u8, kind as u8]);
        } else { hash.update([0_u8]); }
        maybe_owner(&mut hash, commands.selected_enemy(key));
        maybe_owner(&mut hash, commands.current_kind_and_guarded_target(key).1);
        if let Some(outcome) = commands.current_server_outcome(key) {
            hash.update([1_u8]);
            number(&mut hash, outcome);
        } else { hash.update([0_u8]); }
        if let Some(command) = commands.current_selection(key) {
            hash.update([1_u8, command.shooting().id]);
        } else { hash.update([0_u8]); }
        if let Some(permission) = player.player_permissions(key) {
            hash.update([1_u8, u8::from(permission.user_controlled),
                u8::from(permission.can_accept_input),
                u8::from(permission.locked_on)]);
        } else { hash.update([0_u8]); }
        if let Some(shooting) = player.player_shooting_strategy(key) {
            hash.update([1_u8, shooting.id]);
        } else { hash.update([0_u8]); }
        hash.update([u8::from(player.player_should_chase(key))]);
        hash.update([match player.player_cease_fire(key) {
            None => 0_u8, Some(false) => 1, Some(true) => 2,
        }]);
        if let Some(speed) = player.player_matched_speed(key) {
            hash.update([1_u8]);
            scalar(&mut hash, speed);
        } else { hash.update([0_u8]); }
        hash.update([match player.player_carrier_squad(key) {
            None => 0_u8, Some(false) => 1, Some(true) => 2,
        }]);
    }

    // Even when a faction's last squad retires, its learned cache exists
    // until the Level retires. First-seen ship order affects HiveMind
    // targeting tie-breakers; all other sets are canonically sorted.
    let learned = combat.discovery();
    let mut sides = learned.sides().collect::<Vec<_>>();
    sides.sort_unstable();
    for side in sides {
        hash.update(b"D");
        hash.update(side.to_le_bytes());
        let ships = learned.ship_ids_in_sighting_order(side);
        hash.update((ships.len() as u64).to_le_bytes());
        for &id in ships { number(&mut hash, id); }
        sorted_ids(&mut hash, learned.obstacle_ids(side));
        sorted_ids(&mut hash, learned.mining_ids(side));
        sorted_ids(&mut hash, learned.map_object_ids(side));
    }
    format!("{:x}", hash.finalize())
}
