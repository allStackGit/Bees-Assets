# Unified native rewrite handoff - 2026-11-08

Worktree `B:\Bees\unified-native`, branch `rewrite/unified-native`.
Read the attached goal and root AGENTS.md before continuing. The full Unity
replacement goal remains active and incomplete. Original `B:\Bees\Assets` and
live training were untouched. Latest fetch found no newer rewrite commits;
unrelated feat/experiment-queue-ui changes were not merged. No push performed.

## Current integrated increment: command activation through physical attack

Source-authored NativePolicyOnlyMetadata now supports actual HiveMind ranged
command activation instead of rejecting every strategy. Source command settings,
formation offsets/clearances, roster, sprite bounds, real rank inputs and current
mounted weapons supply the command projection. The existing name is retained to
avoid creating another parallel metadata/roster implementation.

Aggressive, Circle, RightSwipe, LeftSwipe and InAndOut run through the canonical
host request/response path, shared director, movement/navigation and automatic
weapon scheduler. Some/all-target range predicates call the same real mounted
contact/bounds/LOS eligibility as ordinary targeting. Defenselessness reads actual
Ship.Firepower. Unsupported non-turret weapon subclasses fail explicitly.

Command snapshot callbacks now borrow &mut CombatWorld and the actual roster;
weapon validity can acquire the real lazy bounds latch. The three ranged callback
families carry the original full SquadLifetime, not just a numeric Squad ID.
Death/retirement returns enemy_alive=false for that exact lifetime even if a new
squad reuses its number. Existing snapshot assemblers receive world through their
range/validity callbacks, avoiding aliasing or a duplicate physics snapshot.

Player Attack carries an explicit enemy lifetime, validates enemy side/liveness
and real snapshot inputs before retiring requests, and activates the same local
Aggressive command. Selected lifetimes deduplicate through existing batch logic.
Renderer hull picking now recognizes enemy targets; the native window right-click
routes enemies to Attack and other locations to movement. Bomber-only/Barge-only
player Attack is explicitly unavailable pending their proper source adapters,
rather than stranding melee ships in the ranged positioning loop.

Exports now additionally capture Ship.Sight as optional source_sight. Scouting
uses that exact value: HiveMindVision.GetEffectiveRange falls back to MaxRange
when Sight==0 and is not Squad.MaxSight. Missing sight keeps Scouting context
unavailable; negative sight is rejected. The already-exported human_side is passed
through the scene/player authority. New Beacon templates have genuine Sight.

## Shared weapons and geometry retained

Prior commit 5ae49a6b1 connected all40 source shooting rankings and all5 mounted
patterns: Single, Dual, Beam, Leaf charged laser, FullShip charged cannon. Manual
point fire takes priority; actual policy bindings bypass automatic selection.
Schema23 captures actual type letter, LastKilled, FleetShip.DamageDone, firepower,
max range, speed, shared FleetShip reference aliases and Time.frameCount. TSV-loss
credit preserves C# int32 wrapping and Fire Barge external-killer attribution.
Native fixed ticks advance captured frame history, not Unity render callbacks.

Only IDs persist in mounted target queues. Ranking/distance scratch is reused per
world; stable caches consume no additional sorting/RNG. Real outgoing reservations
are visible to later mounts within the same control transaction using eventual
projectile IDs. Real source range contacts, lazy bounds, rotated front/center aim,
closest-hull LOS and projectile range-exit lifecycle remain shared. Scout's physical
Beacon spawn installs authored targeting and current parent fleet damage before
range discovery, with fresh newborn LastKilled/bounds. Schemas20-22 keep prior
manual/policy behavior without fabricated automatic inputs.

## Actually executed

External disposable source-contract fixtures/probes are under
`B:\Bees\native-capability-qualification`, not committed tests:
- attack-host.txt: real request_strategy -> receive_frame -> Applied for all5
  ranged strategy commands. Actual timer/movement/automatic physical shots,
  enemy damage, increasing fleet TSV-loss history, interval counters, no policy roots.
- attack-retirement-final.txt: all5 commands kill the original enemy while another
  enemy squad survives; exact roster retirement, command completion and delivery
  to the supplied recording sink retain server outcome101. Physical hit attribution
  is asserted against that same original command outcome.
- player-attack-final.txt: renderer enemy pick -> real player admission -> shared
  Aggressive timer and physical damage; friendly input rejection and duplicate
  selected-lifetime deduplication. player-attack.png visually inspected.
- attack-projection.txt: current contact eligibility, shared bounds latch, range
  disable after its canonical physics sweep, and a replacement roster fixture
  proving an old enemy lifetime never adopts a recycled numeric squad.
- attack-sight.txt: true source Sight0 remains0 despite positive effective vision;
  absent sight stays unavailable and negative sight import is rejected.
- player-support-after-attack.txt: prior support picker/batch execution still passes.
- workspace-attack-final.txt: all106 existing sim tests and workspace all-target/
  all-feature tests pass. headless-attack.txt: no-default-feature check passes.
  Six asset verifiers pass. Existing9 unused_mut warnings remain. No new committed tests.

These are supplied fixtures and a recording in-memory sink, not authentic Unity
exports, production server replies or learned-policy/multi-machine receipts.
The sink receives FinishedCommand but does not implement final TSV accounting or
StoreCommands persistence; do not infer settlement from successful physical attack.
Unity C# compilation/export, physical mouse/window, Linux, variable-render timing,
performance improvement and distributed training remain unverified.

## Highest-value next work / complete original objective

Connect actual BombingRun/Charge activation, proximity and charging-range inputs
using their real common-world special controllers, including player composition
redirects. Add remaining asteroid/MapObject weapon fallback and authored dynamic
factories. Supply game-owned combat/shot statistics and TSV settlement through the
existing outcome and durable notification/StoreCommands boundaries; never invent
shooting credit or count a pending store request as committed.

The complete goal still requires the ordinary scene/game launcher and game modes,
full presentation/sprites/animation/audio/input, scripted queues and Carrier/Drone
lifecycle, authoritative accounting and persistence, connected neural optimizer/
trajectory/checkpoint, distributed learning, evaluation and experiment scheduling,
plus Windows/Linux execution and measured performance. Preserve one gameplay
implementation across the playable client, headless worlds, policies and evaluation.
