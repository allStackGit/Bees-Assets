# Unified native rewrite handoff - 2026-11-07

Worktree `B:\Bees\unified-native`, branch `rewrite/unified-native`.
Fresh fetch found no newer upstream than incorporated `ca8fb367e`.
Prior host/support source settings: `c266ade64`; chosen targets: `34881ba6b`;
origins: `aeb1bc9f7`; player selection/movement: `fcabe9599` / `8d76006dd`.
Original `B:\Bees\Assets` and live training untouched.

## Latest weapon ownership increment

Physical mounted weapons now live in CombatWorld.ship_weapons, separate from
PolicyShipRuntime's decision frame/control/special actions. The one ShipWeapons
owner holds turret slots, cooldown/charging state, last hull rotation, cease-fire
and active beam identity. Configuration, observation, policy masks/requests,
firing, beam growth/teardown and ship death all use it. Policy bind still performs
the same fresh mount reset as before; ordinary non-policy registration/targeting
is NOT enabled by this increment. No second firing controller was added.

Verified: workspace-weapons-owner.txt contains all-target/all-feature compilation
and 106 passing existing tests (including charged/dual/beam weapon paths). Headless
check passed. External src/bin/weapons_owner.rs / weapons-owner.txt exercised
public CombatWorld stepping, five emitted shots, target health100->40, real
weapon observations/action mask and subsequent cease-fire. This supplied-action
probe is not a trained-policy, full Level, live server or Unity parity result.

Highest-value next weapon work: register source non-policy mounts against this
physical owner; port ordinary target eligibility/ranking, aim points and the
non-policy TargetingSequence cadence into the same scheduler. Preserve source
manual/automatic behavior rather than labeling game ships as policy agents.

## Prior integrated window work

- Optional window keys G/P/M/H/R arm Guard/Patrol/Mining/Heal/retreat.
  Left-click targets, left-drag sets Patrol; Backspace/right-click cancels.
  Targeting does not replace squad selection. World/episode switches clear it.
  The executable policy runner handles Support through the real shared host.
- Owned NativePlayerSquadCommands contains only chosen identities/region and
  selection order. NativeHostRuntime::player_command_squads preflights each
  distinct lifetime before issuing the first command. Locked/non-player/stale
  owners skip; real AI requests retire and the canonical command outbox settles.
  Shared resource availability is re-read between activations.
- UI queries cached real hulls, live mining obstacles and Beehive HealCollider
  children (also source-clickable). Heal sends the clicked squad's Beehives in
  actual member order. Friendly targeting uses the selected side; final per-squad
  admission remains authoritative. Combat hulls/contact events are unchanged.
- Source UserMining's non-miner behavior uses existing ordinary movement; it
  does not invent a persistent Mining command. Entirely WarpGate squads skip
  retreat. player_command now returns NativePlayerCommandOutcome::Command or
  ::Move { reachable }, keeping both execution forms explicit.
- Selected ship destination lines read CombatWorld::ship_game_destination,
  the actual installed endpoint, rather than drawing a separate UI order cache.
- Schema20 and source-loaded command settings/shooting remain as before.

## Executed evidence and limits

Windows GNU toolchain: `B:\Bees\Runtime\Toolchains\rust-native`.
Rustup/cargo homes there; cargo/bin and w64devkit/bin on PATH;
RUSTUP_TOOLCHAIN=stable-x86_64-pc-windows-gnu,
RUSTFLAGS=-C link-self-contained=yes. System PATH unchanged.

External qualification: `B:\Bees\native-capability-qualification`.
- player-support-ui.txt / src/bin/player_support_ui.rs: actual presentation
  target picker and selection request builder -> host batch -> run_window.
  Mining6 leaves nearer5 untouched; clicked Heal squad passes [6,2], reserving6;
  Guard chooses14; Patrol honors corners; selected gate6 really warps at tick202.
  Duplicated owner executes once, enemy/stale owners reject, stale revision
  rejects; late strategy9001/9002 discards for every supported command.
- player-support-batch.txt / -locked.txt: two actual Patrols installed; duplicate,
  enemy and stale identities handled, locked owner skipped. Ten Level ticks
  complete navigation and retain canonical first endpoint (50,50) for each.
- player-mining-fallback.txt: non-mining Barge moves toward clicked asteroid
  endpoint(30,0) with no Mining command; source-authorized all-gate squad skips
  retreat. Ten actual host steps advance the ship.
- workspace-support-ui.txt: all-target/all-feature build and 106 existing sim
  tests passed; headless check passed. Other crates have no enabled tests.
- Rendered ui-support-patrol.png from the real software renderer inspected;
  tactical hulls, map, selected endpoint line/cross visible. This is an external
  supplied scene capture, not a window/mouse interaction qualification.

Scenarios/actions/identities and in-memory sink are supplied fixtures, not trained
inference, authentic Unity exports or actual server reservations. Physical mouse
input, C# export execution, Unity/Box2D parity, authentic model switching/learning,
Linux execution and multi-machine behavior remain unverified. The earlier window
control attempt ended at Computer Use app approval timeout.

## Highest-value next work / full objective remains incomplete

Exercise actual window controls when desktop access is available. Complete
source attack inputs and ordinary non-policy weapon targeting/firing,
Chase/StopChasing and scripted queues. PolicyOnlyMetadata still deliberately
rejects HiveMind activation pending full strategy/per-command snapshot adapters.

Full replacement also requires complete game/modes/sprites/audio/UI, Carrier/Drone
lifecycle and remaining parity, connected optimizer/trajectory/checkpoint pipeline,
distributed learning, evaluation/experiments and measured performance. Keep the
same authoritative gameplay for rendered, headless, training and evaluation.
