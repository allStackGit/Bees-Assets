# Unified native rewrite handoff - 2026-11-07

Worktree `B:\Bees\unified-native`, branch `rewrite/unified-native`.
Latest fetch found no newer upstream than `3c76c78a3`, merged in `f7261843e`.
Prior manual weapons `961189b94`; owner split `23c1c442d`; support `ea5ddd1a9`.
Original `B:\Bees\Assets` and live training untouched.

## Latest integrated work

- NativeAuthoredShip now owns actual turret slots and cease_fire. Policy binding
  contains only frame/movement_disabled. Removed the extra turrets_by_ship map;
  importer/builder installs every physical mount once, including non-policy ships.
  No policy frame or fake action is assigned to those game ships. Schema20 unchanged:
  its historical policy_cease_fire field already captures every Ship.IsCeaseFire.
- CombatWorld's ONE mounted scheduler now executes ordinary manual Single,
  DualCannon and BeamCannon fire. Source manual targeting cycles reset missed
  unaligned opportunities instead of inheriting policy readiness latching. Shared
  rotation math, projectile creation, beam ownership/kill, collision and damage
  are reused. All five registered turret fire patterns now accept game manual
  input, including LaserBuilder and FullShipTurret; obsolete rejection removed.
- GameWeaponFired is distinct from PolicyWeaponFired; game shots do not enter
  policy root tracking. LevelRules provides a borrowed current-command resolver
  at launch. Projectiles retain the exact server outcome (or unattributed zero
  for local commands), without a second command registry or per-frame copy.
- NativeHostRuntime::player_fire_squads preflights world/episode, permissions,
  membership, policy ownership and game weapon eligibility. It deduplicates
  selection; no-gun owners are unavailable, unauthorized/unsupported ones reject.
  Weapon control leaves current squad commands and pending strategic requests
  intact. None releases manual fire without resetting its clock.
- Window T arms point-fire, left-click supplies a world point; X releases selected
  weapons. Existing G/P/M/H/R support, batch movement and destination display remain.
  This is latched point-fire; following the moving mouse while a button is held is
  not implemented. Physical mouse/key interaction remains unverified.

## Latest charged game lifecycle

Game LaserBuilder starts its actual 0.8s clip from Aim without the RL cooldown
queue. Release cancels it; cease-fire skips Aim while the existing clip advances
and resolves without firing, then a later uncease starts a fresh clip.

Game FullShipTurret starts/pauses/resumes its 2.75s clip using source alignment.
When stopped, it rotates the true ShipMotion hull at weapon rotation rate and
applies the same delta to every attached turret. Moving manual aim only reads
alignment and does not steer. Release pauses the active clip rather than dropping
its progress. Source SendProjectile's timer callback deactivates the clip; its
animation event launches the projectile. Policy FullShipTurret still queues from
RL readiness, never assists hull steering and fires along its launch-time heading;
game shots use the actual manual point. One mounted scheduler/projectile path.

`weapon_charge` now returns NativeWeaponCharge { elapsed_seconds, event_seconds,
paused }, replacing policy_weapon_charge's tuple. The renderer reads that exact
state for selected-ship blue/paused-orange progress bars. No UI timer exists.

Source inspected: LaserBuilder.cs, FullShipTurret.cs, LaserBuilderControl.cs,
Leaf Cutter Shot Form.anim and Flagship Laser Charging.anim. Fire event times
are 0.8 / 2.75; the relevant prefabs reset Animator state on deactivation.

## Executed evidence and limits

Toolchain: `B:\Bees\Runtime\Toolchains\rust-native` GNU Rust on Windows.
RUSTUP_HOME=<toolchain>/rustup; CARGO_HOME=<toolchain>/cargo; add cargo/bin and
w64devkit/bin to PATH; RUSTUP_TOOLCHAIN=stable-x86_64-pc-windows-gnu;
RUSTFLAGS=-C link-self-contained=yes. No system PATH changes.

External qualification folder: `B:\Bees\native-capability-qualification`.
- game-manual-charged.txt / src/bin/game_manual_fire.rs: source-contract
  fixtures through schema20 import, builder, NoPolicyInference and actual host.
  Single/Dual/Beam still fire; Leafcutter first fires at tick40 (two shots,
  enemy health96); the initially 90-degree flagship turns and fires at tick175
  (one shot, health96). Admission/release and zero policy roots still verified.
- game-charged-phases.txt / src/bin/game_charged_phases.rs: partial Leaf charge
  cancellation, ceased clip resolution/fresh restart, Full paused progress and
  resume, retargeting/hull turn/attached mount rotation. A deliberately short
  flagship timer cancels its animation and produces no premature shots. Paired
  movement-only/manual-aim worlds retain identical heading/position over20 ticks.
  Both advancing and paused Full clips retain their source animation speed while
  ceased, resolve/carry progress correctly and resume after cease-fire ends.
- game-manual-charged-attribution.txt: all five patterns retain supplied server
  Hold identity8001 on real hits, without replacing the command or adding policy
  roots. These IDs are fixtures, not real live server reservations.
- workspace-game-charged.txt: all-target/all-feature build, 106 existing simulation
  tests and headless check passed. No new committed tests; other crates have no
  enabled tests. Policy charged expectations remain in the existing suite.
- full-paused.png / .ppm: inspected real software-renderer capture of the rotated
  hull/selection and paused charge indicator. Orange pixels also confirmed from
  the rendered buffer. This is a supplied scene capture, not mouse qualification.

These are supplied scenarios, weapon inputs and an in-memory notification sink,
not authentic Unity exports, learned policies or live server/distributed runs.
The full collision matrix is a deliberate fixture. C# export execution, exact
Unity/Box2D timing, Linux runtime, physical T/X input and performance unverified.
Earlier desktop-control attempt stopped at app approval timeout; no live fixture
was left running. Do not turn narrower checks into full parity/readiness claims.

## Highest-value next work / full objective remains incomplete

Port ordinary range contacts, target queue/ranking, actual hull/obstacle aim-point
eligibility and automatic TargetingSequence into the SAME mounted scheduler;
then wire chosen-target attack commands. Complete visual sprite animation/audio,
per-weapon statistics/combat timers and persistent game accounting.
Manual point firing supplies no missing automatic targeting semantics.

NativePolicyOnlyMetadata still rejects HiveMind activation until its source
strategy/per-command snapshots exist. A no-model game-only Level works via the
shared runtime/NoPolicyInference and explicit source setup, but a general ordinary
scene launcher, full game/mode/UI/sprite/audio parity, Chase/StopChasing, scripted
queues and Carrier/Drone lifecycle remain incomplete. The full goal also requires
connected optimizer/trajectory/checkpoint, distributed learning, evaluation and
experiments plus measured performance. Preserve one authoritative gameplay path.
