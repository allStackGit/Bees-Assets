# Unified native rewrite handoff - 2026-11-08

Worktree `B:\Bees\unified-native`, branch `rewrite/unified-native`.
Latest fetch found no newer upstream than `3c76c78a3`, merged in `f7261843e`.
Prior manual weapons `961189b94`; owner split `23c1c442d`; support `ea5ddd1a9`.
Original `B:\Bees\Assets` and live training untouched.

## Latest shared target geometry and bounds

Schema22 adds actual source Ship.IsInBoundsLatched (read-only getter, no capture
side effects), GetHalfHeight-minus-configured-front-offset and each turret's
ShouldFireAtFrontOfShip. Reader accepts20/21/22, requires all new data for22 and
passes it through authored entities/builder. NativePhysicalScene exposes its real
schema_version; no guessed geometry or version-independent defaults.

NativeLevelGeometry owns actual bounds/HasObstacles. The lazy monotonic bounds
latch is on the real ShipBody, initially false for newly spawned native bodies;
source capture can set it true, never clear a latch already acquired in native.
Movement and is_weapon_ship_target_valid consume that same state. Real point
orders query it only with obstacles active; previously entered outside ships
still run obstacle/path decisions with ForceBounds path START, while newly
outside ships move directly. Configured Level/order geometry must agree before
mutation. No renderer can latch state through an immutable view.

is_weapon_ship_target_valid follows source enemy/live -> cached range contact ->
lazy bounds -> LOS ordering, independently of cease-fire/aim/cooldown.
weapon_target_point implements rotated authored front/center, actual range-circle
point containment, nearest hull fallback and center fallback when turret is inside
target hull. Vec2.unity_equal preserves Unity's approximate operator rather than
changing Rust PartialEq. Reference checked at:
[Unity Vector2 reference](https://raw.githubusercontent.com/Unity-Technologies/UnityCsReference/master/Runtime/Export/Math/Vector2.cs)
The optional B overlay now draws yellow aim crosses separately from LOS lines;
legacy scenes report missing aim data rather than plotting a fabricated point.

Executed external B:\Bees\native-capability-qualification probes:
- weapon-target-geometry.txt: rotated front/center, exact range boundary/fallback,
  turret inside hull, no latch mutation on inspection/noncontacts/friendlies,
  latch BEFORE blocked LOS rejection, captured false/true outside bounds, actual
  displacement outside after latch, Unity epsilon strict boundary, real point
  orders and bounded path starts, inconsistent orders/missing geometry rejection.
- weapon-target-host.txt: schema22 fixture -> importer -> builder -> NoPolicy host
  -> yellow aim marker and real player movement latch. Partial/nonfinite metadata
  rejected. weapon-target.png visually inspected and marker pixels checked.
- weapon-range-after-target-geometry.txt: prior range lifecycle/scaling probe passes;
  game-manual-after-target-geometry.txt: all5 manual firing fixtures still pass.
- workspace-target-geometry.txt: all106 existing tests/all-target/all-feature pass;
  headless-target-geometry.txt: no-default-feature build passes; six asset verifiers
  pass. No new committed tests. Latest fetch still no newer rewrite commits.

These are supplied source-contract fixtures, not authentic Unity exports. Unity
C# compilation/export execution, physical input and Linux execution are unverified.
Automatic ranking/timer/fire selection remains NOT connected. Dynamic spawn
adapters also need authored front offsets before front-target-point queries can
use those new units. Original Unity checkout and live training untouched.

## Latest authoritative range lifecycle

Mounted weapons now keep ordered enemy range contacts in the physical world,
with an exposed revision for target-cache invalidation. After shared physics,
all authored ranges reuse one lazy ship-grid rebuild (also reusable by Barge and
near-pass queries), layer-filter candidates and test the actual hull. Continuing
contacts retain history; only new simultaneous entrants are sorted by creation
ID. Membership stamps avoid sorting every stable range on every tick. Death,
range disable/reconfiguration, leaving and re-entry are handled on this sweep.

Every mounted shot records its actual source slot. Split children and rocket
continuations inherit it. Previously overlapping projectiles end on source range
exit using their full collider, the same end/reservation/beam/root path as other
causes. Shooter death preserves shots; RocketExplosion/FireTankExplosion remain
excluded. Nominal distance expiry is unchanged. Legacy scenes without authored
ranges do not build the range grid or perform range projectile checks.

StepCounts -> Measurements -> host interval status now carries range grid,
candidate, ship/projectile exact-test, enter/exit and ended-projectile counters.
The optional B overlay title reads actual cached contacts as well as current
geometry. No separate window-owned history or training-only mechanics.

Executed external probes in B:\Bees\native-capability-qualification:
- weapon-range-lifecycle.txt: retained/re-entry order/revisions, friend exclusion,
  independent/disabled slots, target death, layer masks and overflow hulls; Single
  and Beam exit cleanup/reservation release, re-fired beam, surviving shot after
  shooter death, four real split continuations, one policy root completion, growing
  beam collider vs center, and real rocket impact/explosion range exemption.
- Same probe: 1024 mounted ranges yielded10404 broadphase candidates and6324 exact
  enemy-hull tests vs1048576 full pairs; one rebuild and zero steady-sweep cache
  changes. Query-work evidence, NOT a measured throughput improvement.
- weapon-range-host-lifecycle.txt: schema21 clear/blocked and legacy20 through real
  host ticks, cumulative telemetry, sampled status and renderer contact count.
- game-manual-after-range-lifecycle.txt: all five manual patterns still fire at
  ticks31/31/31/40/175 through legacy source-contract scenes.
- workspace-range-lifecycle.txt: all106 existing tests/all-feature/all-target pass;
  headless-range-lifecycle.txt: no-default-feature check passes; six asset verifiers
  pass. No committed new tests. Original Unity checkout/live training untouched.

These remain supplied scenarios, not authentic Unity exports. Unity callback
ordering/timing, physical controls and Linux execution are unverified. MapObject
visibility contributions from RangeCollider are not implemented by this sweep.
Automatic selection/firing remains next; shared bounds and final aim-point queries
are now implemented above for those controllers to consume.

## Latest range/line inspection increment

Schema21 exports each turret's actual child CircleCollider2D offset/radius in
its logical turret frame, collision layer and enabled state. Import/builder
register it on the physical mount for game and policy ships. Schema20 remains
accepted for existing manual/policy execution; missing range queries return an
explicit error. No range is guessed from the nominal weapon range.

CombatWorld exposes read-only weapon_range / query_weapon_target_geometry.
They use the actual hull closest point, authored range overlap, real collision
matrix and Level.HasObstacles with source layer19 primary-obstacle linecasts.
Cached closest-hull math is shared; same-tick changed poses use local geometry
without allocating another polygon or mutating state from presentation.

Client B toggles selected weapons' range circles and clear-blue/blocked-orange
lines. Title counters expose queries, overlaps, obstructions and missing source
inputs. Disabled by default; headless execution performs none of this inspection.
This is geometric inspection, NOT full IsShipValidTarget or automatic targeting.

Executed in B:\Bees\native-capability-qualification:
- weapon-geometry.txt: rotated/concave/inside/edited-pose closest hulls; exact
  concave, circle/tangent, rotated-box and zero-length linecasts; layer/active
  filtering, navigation-only collider exclusion, actual turret offsets, hull-edge
  range overlap, disabled ranges, collision masks and Level.HasObstacles.
- weapon-range-host.txt: schema21 fixture through real no-policy host and software
  renderer: clear 2 queries/1 overlap/0 blocked; blocked 2/1/1; legacy schema20
  0 queries/1 missing mount. Missing schema21 layer rejected. Rendered blue/orange
  pixels checked; weapon-range.png visually inspected. Host tick advances afterward.
- game-manual-after-range.txt: all five existing manual weapon fixtures still fire
  at ticks31/31/31/40/175, damage enemies and preserve admission/release behavior.
- workspace-weapon-geometry.txt: all106 existing tests/all-target/all-feature pass;
  headless-weapon-geometry.txt: no-default-feature check passes; six asset verifiers
  pass. No committed new tests. No performance improvement claimed.

These are supplied contract scenes, not authentic Unity exports. C# exporter
execution, physical B-key interaction and Linux execution remain unverified.
Target ranking and automatic firing still require integration; contact history,
source bounds and final aim-point queries are now implemented above.

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

Build on shared validity/aim-point queries AND authoritative range history: port
target queue/ranking and automatic TargetingSequence into the SAME mounted
scheduler;
then wire chosen-target attack commands. Complete visual sprite animation/audio,
per-weapon statistics/combat timers and persistent game accounting.
Manual point firing supplies no missing automatic targeting semantics. All40
rankings already exist in bees-gameplay/src/commands/targeting.rs for commands and
Bombs, but ordinary weapons must share that ordering rather than duplicate it or
substitute nearest-only targets. Their source LastKilled/FleetShip.DamageDone,
current strategy and preferred enemy squad need real authoritative inputs; do not
invent zeros or copy the canonical roster into metadata. Include source timer
ReadyToFire/GetAimedAtTarget/FireNext semantics and real reservation ownership.

NativePolicyOnlyMetadata still rejects HiveMind activation until its source
strategy/per-command snapshots exist. A no-model game-only Level works via the
shared runtime/NoPolicyInference and explicit source setup, but a general ordinary
scene launcher, full game/mode/UI/sprite/audio parity, Chase/StopChasing, scripted
queues and Carrier/Drone lifecycle remain incomplete. The full goal also requires
connected optimizer/trajectory/checkpoint, distributed learning, evaluation and
experiments plus measured performance. Preserve one authoritative gameplay path.
