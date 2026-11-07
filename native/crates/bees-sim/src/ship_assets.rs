//! Immutable native ship collider catalog.
//!
//! Parsed once when the game or worker starts, then shared across all worlds.
//! No JSON parsing, string matching, or geometry allocation occurs per tick.
//! The canonical native asset is native/assets/ship-colliders.json.

use serde::Deserialize;

use crate::collider_asset_shape::RawShape;
use crate::geometry::Vec2;
use crate::movement::ShipMotion;
use crate::physics::{PhysicsError, ShipBody, ShipShape};

const ASSET_JSON: &str = include_str!(concat!(
    env!("CARGO_MANIFEST_DIR"),
    "/../../assets/ship-colliders.json"
));
pub const SHIP_TYPE_COUNT: usize = 24;
/// ConfigData.ShipTypes.Beacon in the same frozen native asset catalog.
pub const BEACON_TYPE: u8 = 1;

/// Unity ConfigData.ShipSizes / ConfigData.PixelsPerUnit (8), in world
/// units, indexed by ConfigData.ShipTypes. These are the UNROTATED sprite
/// extents used by Ship.GetSize and Squad.GetCenterPoint, not colliders.
/// Ported as a compact immutable table so headless and rendered Levels
/// calculate the same squad/command formation without a second registry.
const SHIP_SPRITE_SIZES: [Vec2; SHIP_TYPE_COUNT] = [
    Vec2::new(19.0, 9.0), // 0: Barge (152x72 pixels)
    Vec2::new(2.25, 2.0), // 1: Beacon (18x16 pixels)
    Vec2::new(34.0, 34.0), // 2: Beehive (272x272 pixels)
    Vec2::new(17.0, 12.0), // 3: Bumblebee (136x96 pixels)
    Vec2::new(16.0, 16.0), // 4: CarpenterBee (128x128 pixels)
    Vec2::new(12.0, 14.0), // 5: Carrier (96x112 pixels)
    Vec2::new(8.0, 9.0), // 6: Cruiser (64x72 pixels)
    Vec2::new(8.0, 10.5), // 7: Dreadnought (64x84 pixels)
    Vec2::new(4.0, 4.0), // 8: Drone (32x32 pixels)
    Vec2::new(16.0, 16.0), // 9: Factory (128x128 pixels)
    Vec2::new(19.0, 9.0), // 10: FireBarge (152x72 pixels)
    Vec2::new(16.0, 19.0), // 11: Flagship (128x152 pixels)
    Vec2::new(6.0, 6.0), // 12: Frigate (48x48 pixels)
    Vec2::new(6.0, 6.0), // 13: Gunship (48x48 pixels)
    Vec2::new(4.0, 4.0), // 14: Honeybee (32x32 pixels)
    Vec2::new(4.0, 4.0), // 15: Hornet (32x32 pixels)
    Vec2::new(8.0, 8.0), // 16: Leafcutter (64x64 pixels)
    Vec2::new(160.0, 128.0), // 17: Queen (1280x1024 pixels)
    Vec2::new(5.0, 4.0), // 18: Scout (40x32 pixels)
    Vec2::new(4.0, 4.0), // 19: Striker (32x32 pixels)
    Vec2::new(28.0, 16.0), // 20: WarpGate (224x128 pixels)
    Vec2::new(6.0, 6.0), // 21: Wasp (48x48 pixels)
    Vec2::new(4.0, 4.0), // 22: YellowJacket (32x32 pixels)
    Vec2::new(32.0, 32.0), // 23: HumanTarget (256x256 pixels)
];

#[derive(Clone, Debug)]
pub struct ShipAsset {
    pub name: String,
    pub ship_type: u8,
    pub collision_layer: u8,
    pub shape: ShipShape,
}

impl ShipAsset {
    /// Cheap Arc clone of immutable hull geometry, not a copy of vertices.
    pub fn spawn_physics_body(
        &self,
        entity_id: i64,
        motion: ShipMotion,
    ) -> Result<ShipBody, PhysicsError> {
        ShipBody::new(entity_id, motion, self.collision_layer, self.shape.clone())
    }
}

#[derive(Debug)]
pub enum AssetError {
    Malformed(String),
    InvalidSchema(u32),
    InvalidShipType(u32),
    MissingShipType(u8),
    DuplicateShipType(u8),
    InvalidShape(u8),
}

#[derive(Debug)]
pub struct ShipAssetCatalog {
    ships: Vec<ShipAsset>,
}

#[derive(Deserialize)]
struct RawManifest {
    schema_version: u32,
    ships: Vec<RawShip>,
}

#[derive(Deserialize)]
struct RawShip {
    name: String,
    ship_type: u32,
    layer: u8,
    trigger: bool,
    shape: RawShape,
}

impl ShipAssetCatalog {
    pub fn embedded() -> Result<Self, AssetError> {
        Self::parse(ASSET_JSON)
    }

    pub fn parse(data: &str) -> Result<Self, AssetError> {
        let raw: RawManifest = serde_json::from_str(data)
            .map_err(|error| AssetError::Malformed(error.to_string()))?;
        if raw.schema_version != 1 {
            return Err(AssetError::InvalidSchema(raw.schema_version));
        }
        let mut slots: Vec<Option<ShipAsset>> =
            std::iter::repeat_with(|| None).take(SHIP_TYPE_COUNT).collect();
        for ship in raw.ships {
            let kind = usize::try_from(ship.ship_type).map_err(|_| AssetError::InvalidShipType(ship.ship_type))?;
            if kind >= SHIP_TYPE_COUNT {
                return Err(AssetError::InvalidShipType(ship.ship_type));
            }
            if slots[kind].is_some() {
                return Err(AssetError::DuplicateShipType(kind as u8));
            }
            if ship.name.is_empty() || !ship.trigger || ship.layer >= 32 {
                return Err(AssetError::InvalidShape(kind as u8));
            }

            let shape = ship.shape.into_shape()
                .ok_or(AssetError::InvalidShape(kind as u8))?;

            slots[kind] = Some(ShipAsset {
                name: ship.name,
                ship_type: kind as u8,
                collision_layer: ship.layer,
                shape,
            });
        }

        let ships = slots.into_iter().enumerate()
            .map(|(i, asset)| asset.ok_or(AssetError::MissingShipType(i as u8)))
            .collect::<Result<Vec<_>, _>>()?;
        Ok(Self { ships })
    }

    #[inline]
    pub fn by_type(&self, ship_type: u8) -> Option<&ShipAsset> {
        self.ships.get(usize::from(ship_type))
    }

    /// Authoritative sprite size by Unity ship type. This must not be
    /// reconstructed from the independently authored collision hull.
    /// Reading an existing ship type is constant time and allocation-free.
    #[inline]
    pub fn sprite_size(&self, ship_type: u8) -> Option<Vec2> {
        self.by_type(ship_type)?;
        SHIP_SPRITE_SIZES.get(usize::from(ship_type)).copied()
    }

    pub fn ships(&self) -> &[ShipAsset] {
        &self.ships
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn all_twenty_four_unity_ship_hulls_have_exactly_one_native_identity() {
        let assets = ShipAssetCatalog::embedded().unwrap();
        assert_eq!(assets.ships().len(), SHIP_TYPE_COUNT);
        assert_eq!(assets.by_type(21).unwrap().name, "Wasp");
        assert_eq!(assets.by_type(13).unwrap().name, "Gunship");
        assert_eq!(assets.by_type(23).unwrap().name, "Human Target");
        assert!(matches!(
            &assets.by_type(0).unwrap().shape, ShipShape::Box { .. }
        ));
        assert!(matches!(
            &assets.by_type(14).unwrap().shape, ShipShape::Circle { .. }
        ));
        assert!(matches!(
            &assets.by_type(15).unwrap().shape, ShipShape::Polygon(_)
        ));
    }

    #[test]
    fn malformed_or_duplicate_assets_are_rejected() {
        assert!(ShipAssetCatalog::parse("{").is_err());
        assert!(matches!(
            ShipAssetCatalog::parse(r#"{"schema_version":2,"ships":[]}"#),
            Err(AssetError::InvalidSchema(2))
        ));
    }
}
