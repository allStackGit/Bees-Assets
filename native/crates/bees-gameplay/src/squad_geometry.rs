//! Unity Squad.GetCenterPoint / SetOffsets over the real game ship poses.
//! Source: Squad.Geometry.TryGetBounds, Squad.Movement.SetOffsets and
//! Ship.Geometry.GetLeft/Right/Top/BottomMostPoint.
//!
//! Ship._size is authored from the source ship sprite and is not a collider
//! dimension. The Level must supply those actual dimensions; a headless
//! worker must use the same prefab metadata, not hull guesses.

use bees_sim::collision::Aabb;
use bees_sim::combat_world::CombatWorld;
use bees_sim::geometry::Vec2;

use crate::squads::NativeSquadEntry;

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct SquadLiveGeometry {
    pub bounds: Aabb,
    pub center: Vec2,
    pub width: f32,
    pub height: f32,
}

#[derive(Clone, Copy, Debug, PartialEq)]
pub struct SquadMemberOffset {
    pub ship_id: i64,
    /// Unity Ship.OffsetFromCenter after Squad.SetOffsets.
    pub offset: Vec2,
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum SquadGeometryError {
    EmptySquad,
    MissingOrDeadShip(i64),
    MissingShipSize(i64),
    InvalidShipSize(i64),
}

/// Compute the source AABB union and formation center from the actual
/// positions plus the authored sprite dimensions (unrotated, as Unity's
/// GetLeftMostPoint/GetTopMostPoint methods do). Update the caller's
/// reusable offsets buffer; no per-tick map or intermediate heap allocation
/// is required.
///
/// The caller invokes this after a MemberLost event (source Ship.Kill calls
/// Squad.SetOffsets for remaining ships) and when preparing a native
/// SquadFormationMember list. Initial authored placement may intentionally
/// preserve prefab offsets for its first SetOffsets; the Level owns that
/// one-time setup flag.
pub fn live_squad_geometry(
    world: &CombatWorld,
    squad: &NativeSquadEntry,
    mut authored_sprite_size: impl FnMut(i64) -> Option<Vec2>,
    offsets: &mut Vec<SquadMemberOffset>,
) -> Result<SquadLiveGeometry, SquadGeometryError> {
    offsets.clear();
    let Some(&first_id) = squad.members.first() else {
        return Err(SquadGeometryError::EmptySquad);
    };
    let first = world.ship_body(first_id)
        .filter(|body| body.active)
        .ok_or(SquadGeometryError::MissingOrDeadShip(first_id))?;
    let first_size = authored_sprite_size(first_id)
        .ok_or(SquadGeometryError::MissingShipSize(first_id))?;
    if !first_size.is_finite() || first_size.x <= 0.0 ||
        first_size.y <= 0.0 {
        return Err(SquadGeometryError::InvalidShipSize(first_id));
    }
    let mut bounds = Aabb {
        min: Vec2::new(
            first.motion.position.x - first_size.x * 0.5,
            first.motion.position.y - first_size.y * 0.5,
        ),
        max: Vec2::new(
            first.motion.position.x + first_size.x * 0.5,
            first.motion.position.y + first_size.y * 0.5,
        ),
    };
    for &ship_id in squad.members.iter().skip(1) {
        let body = world.ship_body(ship_id)
            .filter(|body| body.active)
            .ok_or(SquadGeometryError::MissingOrDeadShip(ship_id))?;
        let size = authored_sprite_size(ship_id)
            .ok_or(SquadGeometryError::MissingShipSize(ship_id))?;
        if !size.is_finite() || size.x <= 0.0 || size.y <= 0.0 {
            return Err(SquadGeometryError::InvalidShipSize(ship_id));
        }
        let position = body.motion.position;
        bounds.min.x = bounds.min.x.min(position.x - size.x * 0.5);
        bounds.min.y = bounds.min.y.min(position.y - size.y * 0.5);
        bounds.max.x = bounds.max.x.max(position.x + size.x * 0.5);
        bounds.max.y = bounds.max.y.max(position.y + size.y * 0.5);
    }
    let center = Vec2::new(
        (bounds.min.x + bounds.max.x) * 0.5,
        (bounds.min.y + bounds.max.y) * 0.5,
    );
    offsets.reserve(squad.members.len());
    for &ship_id in &squad.members {
        let body = world.ship_body(ship_id)
            .expect("validated live squad member");
        offsets.push(SquadMemberOffset {
            ship_id,
            offset: body.motion.position - center,
        });
    }
    Ok(SquadLiveGeometry {
        bounds, center,
        width: bounds.max.x - bounds.min.x,
        height: bounds.max.y - bounds.min.y,
    })
}
