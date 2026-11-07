# Unified native rewrite handoff — 2026-11-07

Worktree `B:\Bees\unified-native`, branch `rewrite/unified-native`.
Latest fetch completed successfully; upstream remains `a31ddf669`.
Earlier local increments: `d77f1c48b` (Windows host/presentation),
`54a74dc68` (authored collision/support), `4d7ee1da1` (specials/Beacon metadata).
The original `B:\Bees\Assets` checkout and live training were not changed.

## Latest implemented increment

- Scene schema **18** exports actual ShipTurningRadius, GetHalfWidth/GetHalfHeight
  and Level.ActivateCollisionAsteroids. Re-export older scenes; do not fabricate
  missing fields. Exporter/importer reject initial active game movement history:
  target coordinates cannot reconstruct waypoint, retry or command history.
- The shared builder installs GameShipSteering profiles for every ship, including
  stationary members needed by squad speed/finalization preflight. Existing policy
  ownership checks keep neural movement authoritative for policy-owned ships.
- NativeLevelRules processes queued paths before canonical combat/movement. The
  Level-owned reusable NavigationRuntime reads real current obstacle geometry,
  runs existing A*, and delivers completions through existing ship/request
  lifecycle validation. Graphics and headless execution use the same path.
- Quiet ticks skip raster/search work. Each pending batch refreshes static and
  enabled dynamic clearance once, covering edited/added/removed objects without
  maintaining another obstacle registry. Large-map/batch cost remains unmeasured;
  static geometry revision tracking and the existing bounded worker scheduler
  are potential optimizations, preserving current ownership semantics.
- Current-episode navigation completion totals reach NativeRuntimeStatus and JSON
  host gauges. These reset with worlds; they are not host-lifetime rate counters.

## Executed evidence

Windows GNU toolchain: `B:\Bees\Runtime\Toolchains\rust-native`.
Use its rustup/cargo homes; add cargo/bin and w64devkit/bin to PATH;
RUSTUP_TOOLCHAIN=stable-x86_64-pc-windows-gnu,
RUSTFLAGS=-C link-self-contained=yes. System PATH was not modified.

- Workspace all-target/all-feature compilation and 106 existing simulation tests
  passed. Other crates currently have no enabled tests.
- Headless/no-default-feature workspace compilation passed. Six asset catalogs
  match their Unity sources. Source diff whitespace checks passed.
- External disposable execution probe:
  `B:\Bees\native-capability-qualification\src\bin\navigation_import.rs`, evidence
  `navigation-import.txt`. An explicit schema-18 scene uses the actual importer,
  builder, NativeLevelRules and CombatWorld. A real layer-19 static obstacle is
  added AFTER initial navigation setup. An imported game-owned ship completes
  one path job, routes around it (maximum lateral distance 27.9873), and arrives
  within sqrt(5) of its destination after 668 fixed ticks. This is a supplied
  contract fixture, not an authentic Unity export or trained model inference.
- Earlier special/Beacon evidence remains in that external qualification folder.
  Those schema-17 fixtures require new source fields before running on schema18;
  old recorded results do not validate changed source.

## Highest-value next work / original goal remains incomplete

Connect normal player squad movement to the native window and host with current
world/episode ownership, actual CanAcceptUserInput/IsUserControlled permissions,
source offsets/clearances, and canonical command finalization/outbox handling.
Source LevelInputManager.MoveSquads calls FinalizeUserCommand then Squad.Move;
ordinary right-click movement is NOT a persistent MoveToPoint HiveMind command.
Reuse bees-sim squad_movement. Preserve outcome reservations; cancel applicable
pending request authority rather than manufacturing selection IDs. Inspect
scripted queue cancellation, Guard unlink/speed restoration and survivor formation
rebasing before claiming complete player-command integration.

Execute the updated C# exporter in Unity, then run authentic schema-18 scenes,
Stage palette/collision matrix and actual v26 models in viewed/headless modes.
C# compilation/execution, Unity/Box2D parity, live model-host operation, Linux
execution, speedup and actual multi-machine behavior remain unverified.

The full replacement still needs complete game/mode/sprite/audio/UI integration,
Carrier/Drone lifecycle and remaining source parity, a connected real optimizer,
trajectory/checkpoint pipeline, distributed learning, evaluation and experiment
orchestration. Maintain one authoritative gameplay implementation throughout.
