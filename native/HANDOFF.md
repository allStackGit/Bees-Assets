# Unified native rewrite handoff - 2026-11-07

Worktree `B:\Bees\unified-native`, branch `rewrite/unified-native`.
Latest fetch found no newer upstream than `3c76c78a3`, merged in `f7261843e`.
Prior weapon-owner split `23c1c442d`; selection/support `ea5ddd1a9` / `c266ade64`.
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
  are reused. Unsupported game charged weapons remain registered but manual
  admission rejects them explicitly; no fabricated charged firing behavior.
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

## Executed evidence and limits

Toolchain: `B:\Bees\Runtime\Toolchains\rust-native` GNU Rust on Windows.
RUSTUP_HOME=<toolchain>/rustup; CARGO_HOME=<toolchain>/cargo; add cargo/bin and
w64devkit/bin to PATH; RUSTUP_TOOLCHAIN=stable-x86_64-pc-windows-gnu;
RUSTFLAGS=-C link-self-contained=yes. No system PATH changes.

External qualification folder: `B:\Bees\native-capability-qualification`.
- game-manual-fire.txt / src/bin/game_manual_fire.rs: schema20 import/builder ->
  NoPolicyInference -> host manual admission -> 120 real host ticks per supported
  pattern. A deliberately unaligned starting turret misses a firing cycle and
  first fires on tick31. Single:4 shots/target health88; Dual:8/health76;
  Beam:4/health84. No policy binding, PolicyWeaponFired or policy root completions.
  Duplicate/stale/enemy/no-gun handling and release verified. Charged manual input
  rejects without failing the Level. All three use the same canonical scheduler.
- game-manual-attribution.txt: same three paths with supplied server-origin Hold
  IDs8001/8002. Manual fire retains Hold; 3/6/4 actual hits retain command8001.
  IDs are fixture inputs, not actual live server reservations.
- workspace-game-manual.txt: all-target/all-feature build, 106 existing sim tests
  and headless check passed. Other crates have no enabled tests.
- player-support-ui-manual.txt re-executes five existing support picker/host
  scenarios. weapons-owner-manual.txt re-executes policy shots/damage/cease-fire.

These are supplied scenarios, weapon inputs and an in-memory notification sink,
not authentic Unity exports, learned policies or live server/distributed runs.
The full collision matrix is a deliberate fixture. C# export execution, exact
Unity/Box2D timing, Linux runtime, physical T/X input and performance unverified.
Earlier desktop-control attempt stopped at app approval timeout; no live fixture
was left running. Do not turn narrower checks into full parity/readiness claims.

## Highest-value next work / full objective remains incomplete

Port ordinary range contacts, target queue/ranking, actual hull/obstacle aim-point
eligibility and automatic TargetingSequence into the SAME mounted scheduler;
then wire chosen-target attack commands. Complete game charged animations/hull
rotation, per-weapon statistics/combat timers and persistent game accounting.
Manual point firing supplies no missing automatic targeting semantics.

NativePolicyOnlyMetadata still rejects HiveMind activation until its source
strategy/per-command snapshots exist. A no-model game-only Level works via the
shared runtime/NoPolicyInference and explicit source setup, but a general ordinary
scene launcher, full game/mode/UI/sprite/audio parity, Chase/StopChasing, scripted
queues and Carrier/Drone lifecycle remain incomplete. The full goal also requires
connected optimizer/trajectory/checkpoint, distributed learning, evaluation and
experiments plus measured performance. Preserve one authoritative gameplay path.
