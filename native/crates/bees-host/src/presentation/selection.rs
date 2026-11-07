//! UI-only squad lifetime selection, always scoped to one actual episode.
//! Membership and permissions are borrowed from the authoritative Level.

use bees_hivemind::session::SquadLifetime;
use bees_sim::{collision::Aabb, physics::PhysicsError, simulation::WorldId};
use super::NativeRenderWorldView;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeSupportMode { Guard, Patrol, Mining, Heal, FullRetreat }

#[derive(Default)]
pub struct NativeSquadSelection {
    world: Option<(WorldId, u64)>,
    squads: Vec<SquadLifetime>,
}

impl NativeSquadSelection {
    pub fn squads(&self) -> &[SquadLifetime] { &self.squads }
    pub fn clear(&mut self) { self.squads.clear(); }

    pub fn synchronize(&mut self, view: &NativeRenderWorldView<'_>) {
        let owner = (view.world_id, view.episode_revision);
        if self.world != Some(owner) {
            self.clear();
            self.world = Some(owner);
        }
        // A squad survives losing its selected representative ship. A
        // retired pooled lifetime cannot inherit somebody else's selection.
        self.squads.retain(|owner| view.roster.get(*owner).is_some());
    }

    pub fn select_ship(&mut self, view: &NativeRenderWorldView<'_>, ship: i64, additive: bool) {
        self.synchronize(view);
        let Some(owner) = view.roster.owning_squad(ship) else { return };
        if !selectable(view, owner) { return; }
        if !additive { self.clear(); }
        if !self.squads.contains(&owner) { self.squads.push(owner); }
    }

    /// Source Selector deduplicates squads touched by the actual selection
    /// collider. Overlap includes hull edges even when centers lie outside.
    pub fn select_box(&mut self, view: &NativeRenderWorldView<'_>, bounds: Aabb)
        -> Result<(), PhysicsError>
    {
        self.synchronize(view);
        let ships = view.combat.physics().ship_ids_overlapping_box(bounds)?;
        self.clear();
        for ship in ships {
            let Some(owner) = view.roster.owning_squad(ship) else { continue };
            if selectable(view, owner) && !self.squads.contains(&owner) {
                self.squads.push(owner);
            }
        }
        Ok(())
    }

    pub fn representative_ships<'a>(&'a self, view: &'a NativeRenderWorldView<'_>)
        -> impl Iterator<Item = i64> + 'a
    {
        self.squads.iter().filter_map(|owner|
            view.roster.get(*owner).and_then(|squad| squad.members.first()).copied())
    }

    pub fn support_command(&mut self, view: &NativeRenderWorldView<'_>,
        action: bees_gameplay::player_inputs::NativePlayerSupportAction,
    ) -> Option<bees_gameplay::player_inputs::NativePlayerSquadCommands> {
        self.synchronize(view);
        (!self.squads.is_empty()).then(|| bees_gameplay::player_inputs::NativePlayerSquadCommands {
            world_id: view.world_id, episode_revision: view.episode_revision,
            squads: self.squads.clone(), action,
        })
    }
}

fn selectable(view: &NativeRenderWorldView<'_>, owner: SquadLifetime) -> bool {
    view.player_authority.player_permissions(owner)
        .is_some_and(|permission| permission.user_controlled && permission.can_accept_input)
}
