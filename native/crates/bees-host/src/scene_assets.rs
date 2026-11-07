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
use bees_sim::ship_assets::{AssetError, ShipAssetCatalog};
use bees_sim::projectile_assets::{ProjectileAssetError, ProjectileAssetCatalog};

#[derive(Debug)]
pub enum NativeSceneAssetError {
    Read { path: PathBuf, error: io::Error },
    AsteroidCatalog(AsteroidAssetError),
    FragmentPalette(FragmentError),
    ShipCatalog(AssetError),
    ProjectileCatalog(ProjectileAssetError),
    CollisionMatrix(CollisionMatrixAssetError),
    StagePalette(FullAsteroidSpawnError),
}

/// Owned per Stage; cloneable immutable collision configuration and
/// catalog-sharing asteroid pool setup. Scene asset loading happens
/// before any policy decisions or physical simulation steps.
pub struct NativeSceneAssets {
    collision_matrix: CollisionMatrix,
    asteroid_stage: FullAsteroidStage,
    fragment_palette: AsteroidFragmentPalette,
    ship_catalog: Arc<ShipAssetCatalog>,
    projectile_catalog: Arc<ProjectileAssetCatalog>,
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
        let collision_matrix = CollisionMatrix::from_unity_export(&matrix_json)
            .map_err(NativeSceneAssetError::CollisionMatrix)?;
        let catalog = Arc::new(CollisionAsteroidCatalog::embedded()
            .map_err(NativeSceneAssetError::AsteroidCatalog)?);
        let asteroid_stage = FullAsteroidStage::from_unity_export(
            &stage_json, expected_stage_name, catalog, authored_map_settings,
        ).map_err(NativeSceneAssetError::StagePalette)?;
        let fragment_palette = AsteroidFragmentPalette::embedded()
            .map_err(NativeSceneAssetError::FragmentPalette)?;
        let ship_catalog = Arc::new(ShipAssetCatalog::embedded()
            .map_err(NativeSceneAssetError::ShipCatalog)?);
        let projectile_catalog = Arc::new(ProjectileAssetCatalog::embedded()
            .map_err(NativeSceneAssetError::ProjectileCatalog)?);
        Ok(Self {
            collision_matrix, asteroid_stage, fragment_palette,
            ship_catalog, projectile_catalog,
        })
    }

    /// The exact same 32-layer physics matrix is installed in the one
    /// NativeLevelRules used by headless and rendered simulation worlds.
    pub fn level_rules(&self) -> NativeLevelRules {
        NativeLevelRules { collision_matrix: self.collision_matrix.clone() }
    }

    pub fn ship_catalog(&self) -> &ShipAssetCatalog { &self.ship_catalog }

    pub fn projectile_catalog(&self) -> Arc<ProjectileAssetCatalog> {
        Arc::clone(&self.projectile_catalog)
    }

    /// Register the Stage's source-authored collision asteroid pool in the
    /// canonical world. A new world/episode gets its own pool accounting
    /// while sharing cached immutable polygon assets across worlds.
    pub fn configure_combat_world(
        &self, world: &mut CombatWorld,
    ) -> Result<(), CombatWorldError> {
        world.configure_full_asteroid_stage(self.asteroid_stage.clone())?;
        world.configure_asteroid_fragments(AsteroidFragmentationConfig {
            catalog: Arc::clone(&self.asteroid_stage.catalog),
            palette: self.fragment_palette.clone(),
            settings: self.asteroid_stage.settings,
        })
    }
}
