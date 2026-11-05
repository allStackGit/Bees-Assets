//! Cross-platform geometry-backed Hive Mind vision benchmark.
//!
//! cargo run --release --manifest-path native/Cargo.toml -p bees-sim \
//!     --example vision_throughput -- --ships-per-side 32 --sweeps 250 --range 60
//!
//! Uses real authored ship collider assets and the actual CombatWorld
//! sighting/learned-visibility path. Positions and sight ranges are an
//! explicitly synthetic stress fixture, not an RL curriculum or Unity
//! gameplay parity claim. It does not simulate firing, movement or MapObjects.

use std::env;
use std::sync::Arc;
use std::time::Instant;

use bees_sim::collision::Aabb;
use bees_sim::combat::ShipVitals;
use bees_sim::combat_world::CombatWorld;
use bees_sim::geometry::Vec2;
use bees_sim::movement::ShipMotion;
use bees_sim::projectile_assets::ProjectileAssetCatalog;
use bees_sim::ship_assets::ShipAssetCatalog;
use bees_sim::vision::{HiveMindObserver, VisionSweepCounts};

#[derive(Clone, Copy)]
struct Options {
    ships_per_side: usize,
    sweeps: u32,
    range: i32,
    spacing: f32,
}

fn options() -> Options {
    let mut opts = Options {
        ships_per_side: 32, sweeps: 250, range: 60, spacing: 24.0,
    };
    let mut args = env::args().skip(1);
    while let Some(option) = args.next() {
        if option == "--help" || option == "-h" {
            println!(
                "Usage: vision_throughput [--ships-per-side 1..256] [--sweeps 1..100000] [--range 1..10000] [--spacing 1..100]"
            );
            std::process::exit(0);
        }
        let value = args.next().unwrap_or_else(|| {
            panic!("missing value for {option}")
        });
        let parsed: u32 = value.parse().unwrap_or_else(|_| {
            panic!("invalid unsigned integer {option}={value}")
        });
        match option.as_str() {
            "--ships-per-side" if (1..=256).contains(&parsed) => {
                opts.ships_per_side = parsed as usize
            }
            "--sweeps" if (1..=100_000).contains(&parsed) => {
                opts.sweeps = parsed
            }
            "--range" if (1..=10_000).contains(&parsed) => {
                opts.range = parsed as i32
            }
            "--spacing" if (1..=100).contains(&parsed) => {
                opts.spacing = parsed as f32
            }
            _ => panic!("unknown option or invalid value: {option}={value}"),
        }
    }
    opts
}

fn main() {
    let options = options();
    let hulls = ShipAssetCatalog::embedded().expect("valid authored ship catalog");
    let shots = Arc::new(ProjectileAssetCatalog::embedded()
        .expect("valid authored projectile catalog"));
    let total = options.ships_per_side * 2;
    let cols = (total as f32).sqrt().ceil() as usize;
    let extent = cols as f32 * options.spacing + 100.0;
    let bounds = Aabb {
        min: Vec2::new(-extent, -extent),
        max: Vec2::new(extent, extent),
    };
    let mut world = CombatWorld::new(bounds, 8.0, shots)
        .expect("valid vision benchmark arena");
    let mut observers = Vec::with_capacity(total);
    for index in 0..total {
        let id = (index + 1) as i64;
        let side = (index % 2 + 1) as i32;
        let ship_type = if side == 1 { 21 } else { 13 };
        let position = Vec2::new(
            (index % cols) as f32 * options.spacing - extent * 0.5,
            (index / cols) as f32 * options.spacing - extent * 0.5,
        );
        world.spawn_ship(
            hulls.by_type(ship_type).expect("authored ship hull"),
            id, side, ShipVitals::new(100, 100, 1000, 0).unwrap(),
            ShipMotion::new(position, 0.0, 90.0, 10.0, 3.0),
        ).expect("unique ship");
        observers.push(HiveMindObserver {
            ship_id: id,
            effective_range: options.range,
            registered: true,
            enabled: true,
        });
    }
    // Separately observe first-discovery cost; repeated passes measure the
    // steady state without resetting the faction memory every frame.
    let first = world.refresh_hivemind_vision(&observers, false)
        .expect("valid source observer roster");
    let started = Instant::now();
    let mut steady = VisionSweepCounts::default();
    for _ in 0..options.sweeps {
        let sample = world.refresh_hivemind_vision(&observers, false)
            .expect("valid native sight geometry");
        steady.merge(&sample);
    }
    let elapsed = started.elapsed().as_secs_f64();
    eprintln!("Vision-only baseline: static real ship colliders, synthetic sight ranges/positions; no weapons, physics stepping or complete RL training.");
    println!("ships_per_side,range,spacing,sweeps,wall_seconds,sweeps_per_second,candidate_ship_pairs,candidate_obstacle_pairs,aabb_rejections,exact_collider_tests,visible_enemy_pairs,first_discoveries");
    println!(
        "{},{},{},{},{:.6},{:.1},{},{},{},{},{},{}",
        options.ships_per_side, options.range, options.spacing,
        options.sweeps, elapsed, options.sweeps as f64 / elapsed,
        steady.live_ship_pairs, steady.unlearned_obstacle_pairs,
        steady.aabb_rejections, steady.exact_collider_tests,
        steady.visible_enemy_pairs, first.first_ship_discoveries,
    );
    assert_eq!(steady.first_ship_discoveries, 0,
        "same static observed world should not rediscover remembered enemies");
    assert_eq!(world.vision_measurements().completed_sweeps,
        u64::from(options.sweeps) + 1);
}
