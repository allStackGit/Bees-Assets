//! UI-only squad lifetime selection, always scoped to one actual episode.
//! Membership and permissions are borrowed from the authoritative Level.

use std::collections::HashSet;

use bees_hivemind::session::SquadLifetime;
use bees_sim::{collision::Aabb, physics::PhysicsError, simulation::WorldId};
use super::NativeRenderWorldView;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum NativeSupportMode { Attack, Guard, Patrol, Mining, Heal, FullRetreat }

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
        // Player authority can also change during a live Level; don't keep
        // previously selected squads eligible for UI commands after that.
        self.squads.retain(|owner| view.roster.get(*owner).is_some() && selectable(view, *owner));
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
    pub fn select_box(&mut self, view: &NativeRenderWorldView<'_>, bounds: Aabb,
        additive: bool) -> Result<(), PhysicsError>
    {
        self.synchronize(view);
        let ships = view.combat.physics().ship_ids_overlapping_box(bounds)?;
        // Ctrl-drag extends the current squad selection just as Ctrl-click
        // does. Preserve existing order and first-hull traversal order for
        // command dispatch, without quadratic membership searches.
        if !additive { self.clear(); }
        let mut seen: HashSet<_> = self.squads.iter().copied().collect();
        for ship in ships {
            let Some(owner) = view.roster.owning_squad(ship) else { continue };
            if selectable(view, owner) && seen.insert(owner) {
                self.squads.push(owner);
            }
        }
        Ok(())
    }

    /// Source LevelInputManager double-click selects every user-eligible
    /// squad containing the clicked ship type. Preserve authored squad
    /// registration order and use the live roster, never a UI-side registry.
    pub fn select_ship_type(&mut self, view: &NativeRenderWorldView<'_>, ship_type: u8) {
        self.synchronize(view);
        self.clear();
        for &owner in view.roster.source_order() {
            let Some(squad) = view.roster.get(owner) else { continue };
            if !selectable(view, owner) { continue; }
            if squad.members.iter().any(|&id| view.combat.ship(id)
                .is_some_and(|ship| !ship.vitals.is_dead() &&
                    ship.ship_type == ship_type)) {
                self.squads.push(owner);
            }
        }
    }

    /// Cycle the source-ordered living user-commandable squads without
    /// inventing their Unity SquadNumber for older scene exports. This
    /// intentionally excludes generated Scout minion squads: they are not
    /// original player squad shortcuts. The exact lifetime is still checked
    /// against the live roster and current player authority.
    pub fn cycle_source_squad(
        &mut self, view: &NativeRenderWorldView<'_>, forward: bool,
    ) -> Option<SquadLifetime> {
        self.synchronize(view);
        let eligible: Vec<_> = view.roster.source_order().iter().copied()
            .filter(|&owner| {
                view.roster.get(owner).is_some_and(|squad|
                    squad.mother_squad.is_none()
                ) && selectable(view, owner)
            }).collect();
        if eligible.is_empty() { return None; }
        let current = self.squads.first().copied()
            .and_then(|owner| eligible.iter().position(|&candidate| candidate == owner));
        let next = match (current, forward) {
            (Some(index), true) => (index + 1) % eligible.len(),
            (Some(index), false) => (index + eligible.len() - 1) % eligible.len(),
            (None, true) => 0,
            (None, false) => eligible.len() - 1,
        };
        let owner = eligible[next];
        self.squads.clear();
        self.squads.push(owner);
        Some(owner)
    }

    pub fn representative_ships<'a>(&'a self, view: &'a NativeRenderWorldView<'_>)
        -> impl Iterator<Item = i64> + 'a
    {
        self.squads.iter().filter_map(|owner| {
            view.roster.get(*owner)?.members.iter().copied().find(|ship_id|
                view.combat.ship(*ship_id).is_some_and(|ship| !ship.vitals.is_dead())
            )
        })
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
