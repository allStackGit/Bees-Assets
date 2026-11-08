# Unified native rewrite handoff - 2026-11-08

Worktree `B:\Bees\unified-native`, branch `rewrite/unified-native`.
Read the attached goal and root AGENTS.md before continuing. The full Unity
replacement goal remains active and incomplete. Original `B:\Bees\Assets` and
live training were untouched. Changes have since been pushed directly to the
rewrite branch on GitHub; reconcile the current branch head before doing further work.
Unrelated feat/experiment-queue-ui changes were not merged.

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
routes enemies to Attack and other locations to movement. Subsequent
commits connected player-only Bomber and Barge composition redirects to the real
BombingRun/Charge activation adapters, failing closed on missing weapon inputs.

Exports now additionally capture Ship.Sight as optional source_sight. Scouting
uses that exact value: HiveMindVision.GetEffectiveRange falls back to MaxRange
when Sight==0 and is not Squad.MaxSight. Missing sight keeps Scouting context
unavailable; negative sight is rejected. The already-exported human_side is passed
through the scene/player authority. New Beacon templates have genuine Sight.

## Further integrated changes (GitHub continuation)

Bomb/Charge activation now validates weapon identity and power against the
actual mounted special weapon before reserving any damage. Failed multi-bomber
assignment restores both the shared reservation ledger and the per-world RNG,
so an unavailable command cannot influence future target selection.

The graphical native client now supports source-style Hold (C): stop an
active Aggressive chase through the normal command finalization/outbox,
without installing a fictitious Hold command. Non-Aggressive Hold is a no-op,
including for pending HiveMind requests. V enables Cease Fire and A restores
Attack on Sight for selected user-controlled squads, except while Heal is
active. Both modes set the existing CombatWorld weapon flags on game or
policy ships, preserve the command/session lifecycle, and reject stale,
locked, or invalid player authority. The UI reports which actions applied.

The additional changes were reviewed in source and pushed to GitHub; they
have NOT been compiled or exercised with the actual native window/Unity
export on this assistant environment (Rust tooling is unavailable).
A subsequent pass added one shared CombatWorld outcome-counter helper used by
both native gameplay and physics-only rule adapters. It aggregates actual
enemy/friendly HitEvent counts and TSV losses, plus finalized policy shot
roots/contact/near misses through cumulative Simulation measurements and host
status intervals. The measurements do NOT settle command/shooting OutcomeIds;
that persistent delayed-hit accounting remains to be implemented. Avoid
creating a second per-shot history solely to compute these counters.

A newer commit forwards immutable CombatHit and UnattributedDamage records
from the authoritative physical events through the same ordered, acknowledged
Level notification outbox. Core fixed-step events are placed before the
same-tick FinishedCommand; later timer events retain their own tick order.
The durable diagnostic sink serializes structured damage and counts only
synchronized records, while Simulation/status count the same attributed and
unattributed physical damage. This is NOT a StoreCommands accounting sink.
The Level now captures each damage event's attacker command (original shot ID
first, impact-time command fallback for synchronous damage) and defender's
active command BEFORE casualty retirement, using reusable per-tick scratch.
Timed callbacks can attribute an impact to a just-finished command.
Finished/Aborted command records durably encode the original server strategy
and both reservation IDs independently of BombingRun/Charge dispatch.
Finished commands also retain the source-selected enemy-shooting admission;
the settlement helper cannot credit an ineligible shooting reservation.
The current follow-on implemented NativeCommandCreditLedger as the one
episode-owned command/shooting TSV projection. It consumes physical hits and
unattributed victim losses, retains credit for commands superseded before a
shot lands, and includes actual mined/healed capability TSV on finalization.
HiveMind replacements now retain their predecessor in the authoritative
Level notification outbox instead of dropping the returned superseded value.
On a valid EpisodeFinal the Level emits one CommandOutcomesReady batch; on
EpisodeAborted it discards all known command/shooting OutcomeIds. It never
commits a FinishedCommand while delayed projectiles can still hit.

DurableDiagnosticNotifications synchronizes that batch into the existing
per-run journal before exposing it. NativeHostRuntime then queues the
original batch into DurableHiveMindConnection, which checkpoints its canonical
StoreCommands hash/retry payload before the sink retires it. The host queues
completed batches even when a later window step fails, and also on stop/reset.
**Queued is not transmitted or acknowledged**: a real socket still has to send
pending_store_hashes/retry_payload and process 200/409 acknowledgements.
Explicit targeting-result credit and stale-reply discard actions still need
their real input/settlement path. The gap between journal fsync and connection
checkpoint also needs restart-time replay/reconciliation before autonomous
crash recovery can be claimed. No added shot history, made-up target TSV or
premature per-command StoreCommands credit was introduced. This code remains
source-reviewed only; native Rust compilation/runtime were not available.
Earlier fixture qualifications below predate these edits.

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

BombingRun/Charge physical activation, source proximity/charging-range
inputs and player composition redirects are now present; their live Unity
behavior remains unverified. Add remaining asteroid/MapObject weapon fallback
and authored dynamic factories. Supply game-owned combat/shot statistics and
TSV settlement through the existing outcome and durable notification/
StoreCommands boundaries; never invent shooting credit or count a pending
store request as committed.

The complete goal still requires the ordinary scene/game launcher and game modes,
full presentation/sprites/animation/audio/input, scripted queues and Carrier/Drone
lifecycle, authoritative accounting and persistence, connected neural optimizer/
trajectory/checkpoint, distributed learning, evaluation and experiment scheduling,
plus Windows/Linux execution and measured performance. Preserve one gameplay
implementation across the playable client, headless worlds, policies and evaluation.
