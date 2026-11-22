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
use std::sync::{Arc, OnceLock};
use std::time::Instant;
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

// Embedded collider manifests are immutable and identical for every Stage.
// Reuse their already validated Arc-backed geometry across campaign missions,
// parallel training worlds and repeated matches. Stage-specific collision
// matrices, prefab selection, rules and asset digests remain per Stage.
static SHIP_CATALOG: OnceLock<Arc<ShipAssetCatalog>> = OnceLock::new();
static PROJECTILE_CATALOG: OnceLock<Arc<ProjectileAssetCatalog>> = OnceLock::new();
static ASTEROID_CATALOG: OnceLock<Arc<CollisionAsteroidCatalog>> = OnceLock::new();
static BOX_CATALOG: OnceLock<Arc<BoxObstacleCatalog>> = OnceLock::new();
static POLYGON_CATALOG: OnceLock<Arc<PolygonObstacleCatalog>> = OnceLock::new();
static FRAGMENT_PALETTE: OnceLock<AsteroidFragmentPalette> = OnceLock::new();

/// Real setup work, distinct from in-game simulation steps per second.
/// Cold parses include racing initializations; warm reuse is allocation-free.
#[derive(Clone, Copy, Debug, Default, serde::Serialize)]
pub struct NativeSceneAssetLoadMeasurements {
    pub embedded_catalog_parses: u8,
    pub embedded_catalog_reuses: u8,
    pub elapsed_wall_micros: u64,
}

/// Invalid embedded manifests are still reported through the exact existing
/// typed error. Never cache an error as a default/empty gameplay catalog.
fn shared_catalog<T>(
    cache: &OnceLock<Arc<T>>,
    load: impl FnOnce() -> Result<T, NativeSceneAssetError>,
    measurements: &mut NativeSceneAssetLoadMeasurements,
) -> Result<Arc<T>, NativeSceneAssetError> {
    if let Some(catalog) = cache.get() {
        measurements.embedded_catalog_reuses += 1;
        return Ok(Arc::clone(catalog));
    }
    let loaded = Arc::new(load()?);
    measurements.embedded_catalog_parses += 1;
    Ok(Arc::clone(cache.get_or_init(|| loaded)))
}

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
    load_measurements: NativeSceneAssetLoadMeasurements,
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
        let started = Instant::now();
        let mut load_measurements = NativeSceneAssetLoadMeasurements::default();
        let collision_matrix = CollisionMatrix::from_unity_export(matrix_json)
            .map_err(NativeSceneAssetError::CollisionMatrix)?;
        let catalog = shared_catalog(&ASTEROID_CATALOG, || {
            CollisionAsteroidCatalog::embedded().map_err(NativeSceneAssetError::AsteroidCatalog)
        }, &mut load_measurements)?;
        let asteroid_stage = FullAsteroidStage::from_unity_export(
            stage_json, expected_stage_name, catalog, authored_map_settings,
        ).map_err(NativeSceneAssetError::StagePalette)?;
        let fragment_palette = if let Some(palette) = FRAGMENT_PALETTE.get() {
            load_measurements.embedded_catalog_reuses += 1;
            palette.clone()
        } else {
            let loaded = AsteroidFragmentPalette::embedded()
                .map_err(NativeSceneAssetError::FragmentPalette)?;
            load_measurements.embedded_catalog_parses += 1;
            FRAGMENT_PALETTE.get_or_init(|| loaded).clone()
        };
        let ship_catalog = shared_catalog(&SHIP_CATALOG, || {
            ShipAssetCatalog::embedded().map_err(NativeSceneAssetError::ShipCatalog)
        }, &mut load_measurements)?;
        let projectile_catalog = shared_catalog(&PROJECTILE_CATALOG, || {
            ProjectileAssetCatalog::embedded().map_err(NativeSceneAssetError::ProjectileCatalog)
        }, &mut load_measurements)?;
        let box_catalog = shared_catalog(&BOX_CATALOG, || {
            BoxObstacleCatalog::embedded().map_err(NativeSceneAssetError::BoxCatalog)
        }, &mut load_measurements)?;
        let polygon_catalog = shared_catalog(&POLYGON_CATALOG, || {
            PolygonObstacleCatalog::embedded().map_err(NativeSceneAssetError::PolygonCatalog)
        }, &mut load_measurements)?;
        let mut digest = Sha256::new();
        for source in [&matrix_json, &stage_json] {
            digest.update((source.len() as u64).to_le_bytes());
            digest.update(source.as_bytes());
        }
        load_measurements.elapsed_wall_micros =
            started.elapsed().as_micros().min(u64::MAX as u128) as u64;
        Ok(Self {
            stage_name: expected_stage_name.to_owned(),
            source_assets_sha256: format!("{:x}", digest.finalize()),
            collision_matrix, asteroid_stage, fragment_palette,
            ship_catalog, projectile_catalog, box_catalog, polygon_catalog,
            load_measurements,
        })
    }

    /// The exact same 32-layer physics matrix is installed in the one
    /// NativeLevelRules used by headless and rendered simulation worlds.
    pub fn level_rules(&self) -> NativeLevelRules {
        NativeLevelRules { collision_matrix: self.collision_matrix.clone() }
    }

    pub fn source_assets_sha256(&self) -> &str { &self.source_assets_sha256 }

    pub fn stage_name(&self) -> &str { &self.stage_name }

    /// Original Stage.Prefabs.CollisionAsteroidPrefabs is the legal source
    /// palette for both its random asteroid pool and explicitly pre-placed
    /// source asteroids. Keep this ownership in the one loaded Stage.
    pub fn has_stage_asteroid_prefab(&self, prefab: u8) -> bool {
        self.asteroid_stage.prefabs.contains(&prefab)
    }

    /// A pre-placed full asteroid retains its original Create-time speed.
    /// It must belong to the same Stage's [2, 2 + RandomInt(max)) pool
    /// range as a newly spawned asteroid, not merely have a valid prefab.
    pub fn has_stage_asteroid_speed(&self, speed: i32) -> bool {
        let delta = i64::from(speed) -
            i64::from(bees_sim::asteroid_spawn::MINIMUM_ASTEROID_SPEED);
        let max = i64::from(self.asteroid_stage.settings.asteroid_max_speed_exclusive);
        delta >= 0 && (if max == 0 { delta == 0 } else { delta < max })
    }

    pub fn load_measurements(&self) -> NativeSceneAssetLoadMeasurements {
        self.load_measurements
    }

    pub fn ship_catalog(&self) -> &ShipAssetCatalog { &self.ship_catalog }

    /// Share immutable prefab geometry and sprite sizes among all Levels.
    pub fn shared_ship_catalog(&self) -> Arc<ShipAssetCatalog> {
        Arc::clone(&self.ship_catalog)
    }
    pub fn collision_asteroid_catalog(&self) -> &CollisionAsteroidCatalog {
        &self.asteroid_stage.catalog
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
        // Both configurations use the same map-local Stage settings.
        // Extract the fragment inputs before moving the owned Stage into
        // the world; avoid duplicating its authored prefab roster for every
        // parallel physical Level.
        let fragments = AsteroidFragmentationConfig {
            catalog: Arc::clone(&stage.catalog),
            palette: self.fragment_palette.clone(),
            settings: stage.settings,
        };
        world.configure_full_asteroid_stage(stage)?;
        world.configure_asteroid_fragments(fragments)
    }
}
