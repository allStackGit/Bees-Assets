//! Authoritative game scene asset loader shared by Windows/Linux clients.
//!
//! The editor exports the 2D layer collision matrix and each Stage's exact
//! ordinary asteroid prefab order. The host loads them ONCE at setup;
//! the same collision matrix and authored asteroid pool configure every
//! native Level world, independent of whether it is rendered or headless.
//! No guessed fallback, duplicated Unity physics, or per-tick disk access.

use std::fs;
use std::io;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use sha2::{Digest, Sha256};

use bees_gameplay::level_rules::NativeLevelRules;
use bees_sim::asteroid_assets::{
    AsteroidAssetError, CollisionAsteroidCatalog,
};
use bees_sim::asteroid_fragments::{AsteroidFragmentPalette, FragmentError, FragmentSpawnSettings};
use bees_sim::asteroid_spawn::{
    FullAsteroidSpawnError, FullAsteroidStage,
};
use bees_sim::combat_world::{AsteroidFragmentationConfig, CombatWorld, CombatWorldError};
use bees_sim::physics::{CollisionMatrix, CollisionMatrixAssetError};
use bees_sim::collision::Aabb;
use bees_sim::geometry::Vec2;
use bees_sim::ship_assets::{AssetError, ShipAssetCatalog};
use bees_sim::projectile_assets::{ProjectileAssetError, ProjectileAssetCatalog};
use bees_sim::box_obstacle_assets::{BoxObstacleCatalog, BoxObstacleAssetError};
use bees_sim::polygon_obstacle_assets::{PolygonObstacleCatalog, PolygonObstacleAssetError};

#[derive(Debug)]
pub enum NativeSceneAssetError {
    Read { path: PathBuf, error: io::Error },
    AsteroidCatalog(AsteroidAssetError),
    FragmentPalette(FragmentError),
    ShipCatalog(AssetError),
    ProjectileCatalog(ProjectileAssetError),
    BoxCatalog(BoxObstacleAssetError),
    PolygonCatalog(PolygonObstacleAssetError),
    CollisionMatrix(CollisionMatrixAssetError),
    StagePalette(FullAsteroidSpawnError),
}

/// Owned per Stage; cloneable immutable collision configuration and
/// catalog-sharing asteroid pool setup. Scene asset loading happens
/// before any policy decisions or physical simulation steps.
pub struct NativeSceneAssets {
    stage_name: String,
    source_assets_sha256: String,
    collision_matrix: CollisionMatrix,
    asteroid_stage: FullAsteroidStage,
    fragment_palette: AsteroidFragmentPalette,
    ship_catalog: Arc<ShipAssetCatalog>,
    projectile_catalog: Arc<ProjectileAssetCatalog>,
    box_catalog: Arc<BoxObstacleCatalog>,
    polygon_catalog: Arc<PolygonObstacleCatalog>,
}

impl NativeSceneAssets {
    pub fn load(
        unity_collision_matrix_path: &Path,
        unity_stage_palette_path: &Path,
        expected_stage_name: &str,
        authored_map_settings: FragmentSpawnSettings,
    ) -> Result<Self, NativeSceneAssetError> {
        let read = |path: &Path| {
            fs::read_to_string(path).map_err(|error| NativeSceneAssetError::Read {
                path: path.to_path_buf(), error,
            })
        };
        let matrix_json = read(unity_collision_matrix_path)?;
        let stage_json = read(unity_stage_palette_path)?;
        Self::from_asset_json(&matrix_json, &stage_json, expected_stage_name, authored_map_settings)
    }

    /// Native gameplay and training authoring can provide a collision layer
    /// matrix and exact ordered asteroid prefab roster directly. These go
    /// through the same symmetric-matrix and prefab catalog validators as
    /// legacy Stage exports; no second physics or asteroid rules are added.
    pub fn from_native_stage(
        rows: [u32; 32], prefab_numbers: &[u8], expected_stage_name: &str,
        authored_map_settings: FragmentSpawnSettings,
    ) -> Result<Self, NativeSceneAssetError> {
        let matrix = serde_json::json!({
            "schema_version": 1,
            "rows": rows,
        }).to_string();
        let palette = serde_json::json!({
            "schema_version": 1,
            "stage_name": expected_stage_name,
            "asteroid_max_speed_exclusive":
                authored_map_settings.asteroid_max_speed_exclusive,
            "prefab_numbers": prefab_numbers,
        }).to_string();
        Self::from_asset_json(&matrix, &palette, expected_stage_name, authored_map_settings)
    }

    fn from_asset_json(
        matrix_json: &str, stage_json: &str, expected_stage_name: &str,
        authored_map_settings: FragmentSpawnSettings,
    ) -> Result<Self, NativeSceneAssetError> {
        let collision_matrix = CollisionMatrix::from_unity_export(matrix_json)
            .map_err(NativeSceneAssetError::CollisionMatrix)?;
        let catalog = Arc::new(CollisionAsteroidCatalog::embedded()
            .map_err(NativeSceneAssetError::AsteroidCatalog)?);
        let asteroid_stage = FullAsteroidStage::from_unity_export(
            stage_json, expected_stage_name, catalog, authored_map_settings,
        ).map_err(NativeSceneAssetError::StagePalette)?;
        let fragment_palette = AsteroidFragmentPalette::embedded()
            .map_err(NativeSceneAssetError::FragmentPalette)?;
        let ship_catalog = Arc::new(ShipAssetCatalog::embedded()
            .map_err(NativeSceneAssetError::ShipCatalog)?);
        let projectile_catalog = Arc::new(ProjectileAssetCatalog::embedded()
            .map_err(NativeSceneAssetError::ProjectileCatalog)?);
        let box_catalog = Arc::new(BoxObstacleCatalog::embedded()
            .map_err(NativeSceneAssetError::BoxCatalog)?);
        let polygon_catalog = Arc::new(PolygonObstacleCatalog::embedded()
            .map_err(NativeSceneAssetError::PolygonCatalog)?);
        let mut digest = Sha256::new();
        for source in [&matrix_json, &stage_json] {
            digest.update((source.len() as u64).to_le_bytes());
            digest.update(source.as_bytes());
        }
        Ok(Self {
            stage_name: expected_stage_name.to_owned(),
            source_assets_sha256: format!("{:x}", digest.finalize()),
            collision_matrix, asteroid_stage, fragment_palette,
            ship_catalog, projectile_catalog, box_catalog, polygon_catalog,
        })
    }

    /// The exact same 32-layer physics matrix is installed in the one
    /// NativeLevelRules used by headless and rendered simulation worlds.
    pub fn level_rules(&self) -> NativeLevelRules {
        NativeLevelRules { collision_matrix: self.collision_matrix.clone() }
    }

    pub fn source_assets_sha256(&self) -> &str { &self.source_assets_sha256 }

    pub fn stage_name(&self) -> &str { &self.stage_name }

    pub fn ship_catalog(&self) -> &ShipAssetCatalog { &self.ship_catalog }

    /// Share immutable prefab geometry and sprite sizes among all Levels.
    pub fn shared_ship_catalog(&self) -> Arc<ShipAssetCatalog> {
        Arc::clone(&self.ship_catalog)
    }
    pub fn box_catalog(&self) -> &BoxObstacleCatalog { &self.box_catalog }
    pub fn polygon_catalog(&self) -> &PolygonObstacleCatalog { &self.polygon_catalog }

    pub fn projectile_catalog(&self) -> Arc<ProjectileAssetCatalog> {
        Arc::clone(&self.projectile_catalog)
    }

    /// Register the Stage's source-authored collision asteroid pool in the
    /// canonical world. A new world/episode gets its own pool accounting
    /// while sharing cached immutable polygon assets across worlds.
    pub fn configure_combat_world(
        &self, world: &mut CombatWorld,
    ) -> Result<(), CombatWorldError> {
        self.configure_combat_world_for_map(world, None)
    }

    /// The Stage supplies immutable asteroid prefab identities, maximum
    /// speed and training durability; each Level supplies its own actual
    /// map-local spawn rectangle. No alternate world or random selection
    /// rules are introduced for multi-map headless training.
    pub fn configure_combat_world_for_map(
        &self,
        world: &mut CombatWorld,
        map_geometry: Option<(Aabb, Vec2)>,
    ) -> Result<(), CombatWorldError> {
        let mut stage = self.asteroid_stage.clone();
        if let Some((bounds, half_extents)) = map_geometry {
            stage.settings.map_bounds = bounds;
            stage.settings.half_map_extents = half_extents;
        }
        world.configure_full_asteroid_stage(stage.clone())?;
        world.configure_asteroid_fragments(AsteroidFragmentationConfig {
            catalog: Arc::clone(&stage.catalog),
            palette: self.fragment_palette.clone(),
            settings: stage.settings,
        })
    }
}
