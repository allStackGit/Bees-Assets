//! Native game squad composition on the one physical world construction
//! path. Merging multiple authentic squads never duplicates a ship, training
//! engine, fleet value, policy controller or weapon and has no runtime cost.

use super::*;

impl NativePhysicalScene {
    pub fn merge_native_layout_squads(
        &mut self, groups: &[NativeSquadMerge],
    ) -> Result<usize, NativeSnapshotError> {
        if groups.is_empty() { return Ok(0); }
        let invalid = NativeSnapshotError::InvalidNativeLayout;
        if self.source_episode.is_some() || self.source_policy_group.is_some() ||
            !self.source_policy_bindings.is_empty()
        {
            return Err(invalid("game squad composition cannot modify policy ownership"));
        }
        let by_id = self.source_squads.iter().enumerate()
            .map(|(index, squad)| (squad.item_id, index))
            .collect::<HashMap<_, _>>();
        let mut all_squad_ids = HashSet::new();
        let mut donor_ids = HashSet::new();
        let mut revisions = Vec::with_capacity(groups.len());
        // Validate every group before mutating any source roster. A donor
        // cannot appear in multiple groups or also be the next primary.
        for group in groups {
            if group.primary_squad_item_id <= 0 ||
                group.merged_squad_item_ids.is_empty() ||
                group.merged_squad_item_ids.len() > 1023 ||
                !all_squad_ids.insert(group.primary_squad_item_id)
            {
                return Err(invalid("invalid or duplicated native squad group identity"));
            }
            let &primary = by_id.get(&group.primary_squad_item_id)
                .ok_or(invalid("squad grouping primary has no real command owner"))?;
            let leader = &self.source_squads[primary];
            if leader.members.is_empty() || leader.hivemind.is_some() ||
                leader.is_carrier_squad == Some(true)
            {
                return Err(invalid("squad grouping requires ordinary game command owners"));
            }
            let mut all_members = leader.members.clone();
            for &id in &group.merged_squad_item_ids {
                if id <= 0 || !all_squad_ids.insert(id) || !donor_ids.insert(id) {
                    return Err(invalid("one source squad can join only one group"));
                }
                let &index = by_id.get(&id)
                    .ok_or(invalid("grouped squad has no original source command owner"))?;
                let donor = &self.source_squads[index];
                if donor.side != leader.side ||
                    donor.player_permissions.user_controlled !=
                        leader.player_permissions.user_controlled ||
                    donor.player_permissions.can_accept_input !=
                        leader.player_permissions.can_accept_input ||
                    donor.player_permissions.locked_on !=
                        leader.player_permissions.locked_on ||
                    donor.shooting != leader.shooting ||
                    donor.cease_fire != leader.cease_fire ||
                    donor.should_chase != leader.should_chase ||
                    donor.matching_speed != leader.matching_speed ||
                    donor.custom_color_rgb != leader.custom_color_rgb ||
                    donor.saved_squad_id.is_some() ||
                    donor.hotkey_number.is_some() ||
                    donor.original_squad_count.is_some() ||
                    donor.hivemind.is_some() ||
                    donor.is_carrier_squad == Some(true)
                {
                    return Err(invalid("grouped squads must share one ordinary player/AI command contract"));
                }
                all_members.extend_from_slice(&donor.members);
            }
            if all_members.len() > 1024 ||
                all_members.iter().copied().collect::<HashSet<_>>().len() != all_members.len()
            {
                return Err(invalid("merged squad exceeds source squad member capacity or duplicates ships"));
            }
            let mut sum = Vec2::ZERO;
            for &ship_id in &all_members {
                let ship = self.entities.iter().find_map(|entity| match entity {
                    NativeAuthoredEntity::Ship(ship) if ship.entity_id == ship_id =>
                        Some(ship),
                    _ => None,
                }).ok_or(invalid("grouped squad lacks a real physical ship"))?;
                if ship.side != leader.side || ship.policy.is_some() ||
                    ship.motion.is_policy_controlled ||
                    ship.special.is_some() || ship.special_weapon.is_some() ||
                    ship.drone_carrier_id.is_some() ||
                    ship.vitals.is_dead() ||
                    ship.motion.is_moving || ship.motion.has_target_coordinates ||
                    ship.motion.body_velocity.length_squared() != 0.0 ||
                    !self.source_formation.contains_key(&ship_id)
                {
                    return Err(invalid("native groups require stationary ordinary source ships"));
                }
                sum += ship.motion.position;
            }
            let center = sum * (all_members.len() as f32).recip();
            revisions.push((primary, all_members, center));
        }

        // No deletion of gameplay or fleet entities: just replace the
        // initial source command owners and set physical formation offsets
        // from their present, collision-tested positions.
        for (index, ids, center) in &revisions {
            for &id in ids {
                let position = self.entities.iter().find_map(|entity| match entity {
                    NativeAuthoredEntity::Ship(ship) if ship.entity_id == id =>
                        Some(ship.motion.position),
                    _ => None,
                }).expect("validated physical group member");
                self.source_formation.get_mut(&id)
                    .expect("validated source formation offset").offset = position - *center;
            }
            self.source_squads[*index].members = ids.clone();
        }
        self.source_squads.retain(|squad| !donor_ids.contains(&squad.item_id));
        Ok(groups.len())
    }
}
