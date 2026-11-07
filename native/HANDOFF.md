# Unified native rewrite handoff — 2026-11-07

Worktree `B:\Bees\unified-native`, branch `rewrite/unified-native`.
Fetched and merged all 56 upstream commits through `ca8fb367e` in `ed97aa136`.
The merge preserves local rendering, collision/support, ship-special/Beacon and
navigation work alongside upstream authenticated MA-POCA ownership, verified
model generations, clean episode-boundary switches and evaluation segments.
Earlier local commits remain in history. Backup branch:
`backup/native-before-upstream-20261107`; pre-merge player-control WIP also remains
in `stash@{0}` as a safety copy. Do not reapply it over the finished implementation.
The original `B:\Bees\Assets` checkout and live training were not changed.

## Latest integrated work

- Normal right-click movement now goes from NativeClient through the same host,
  roster, formation/pathfinding and CombatWorld used for headless gameplay.
  A selected ship identifies its whole squad. The host checks exact source
  permissions and world/episode identity; rejects stale/dead/policy-owned inputs.
- Player input finalizes a prior real command as Superseded, retaining its actual
  outcome reservations in the existing notification outbox. It retires pending
  socket-local strategy/targeting authority so late replies cannot override the
  move; replies still produce explicit reservation-discard obligations.
- Movement preflights all members. Tokens are rejected across changed ticks,
  ownership, permissions, membership or formation inputs. Execution failures mark
  the Level failed and the runner follows existing abort/settlement cleanup.
- Source schema **19** combines upstream group/episode/team movement metadata
  with actual collision/support/special/steering data and player squad permissions,
  lock state, stored offsets, clearances and Level.HasObstacles. Re-export older
  scenes; do not invent missing fields. Initial active commands/scripted queues
  and movement history are rejected because this is not a checkpoint format.
- Survivor formation offsets now update in imported metadata after casualties.
  Right-click uses Source FinalizeUserCommand then Squad.Move semantics; it does
  not allocate a fake persistent HiveMind MoveToPoint selection.
- Upstream model-boundary scheduling/evaluation reporting remains in the viewed
  runner; its one-step pacing does not skip model-update eligibility boundaries.

## Executed evidence and limits

Windows GNU toolchain: `B:\Bees\Runtime\Toolchains\rust-native`.
Use rustup/cargo homes there; add cargo/bin and w64devkit/bin to PATH;
RUSTUP_TOOLCHAIN=stable-x86_64-pc-windows-gnu,
RUSTFLAGS=-C link-self-contained=yes. System PATH was not modified.

- Merged all-target/all-feature workspace compilation and 106 existing simulation
  tests passed. Headless/no-default-feature compilation passed. Run results are in
  `B:\Bees\native-capability-qualification\workspace-latest.txt`.
- Six asset catalogs match their Unity sources. Python policy worker syntax
  compilation passed. Diff whitespace checks passed.
- External disposable fixture `src\bin\player_host.rs` in that qualification
  folder uses the real schema-19 importer/builder, source group actor IDs,
  NativeHostRuntime, command director/outbox and fixed-step rules. Evidence:
  `player-host-latest.txt`. It rejects stale/non-player input, supersedes a real
  installed Hold while retaining fixture outcome IDs 8001/8002, discards late
  strategy IDs 9001/9002, and routes the player ship around a real layer-19 wall
  to within sqrt(5) of the destination after 668 fixed ticks (one path job).
  These are explicitly supplied fixture actions and identities, not real trained
  inference, authentic Unity export, live server reservations or durable PPO.
- Actual mouse interaction remains unverified. An attempted isolated window
  fixture was interrupted; no target window/process handle remained afterward.
  C# exporter compilation/execution, authentic model reload/evaluation runs,
  Unity/Box2D parity, Linux execution and multi-machine behavior are unverified.

## Next work / full objective remains incomplete

Exercise actual mouse movement and multi-ship formations on authentic exported
schema-19 Levels with real v26 policies and the Stage palette/collision matrix.
Complete player stop/attack/guard/mining/healing controls and source scripted queue
ownership using the existing command/director/outbox system. Preserve real
reservation accounting; do not substitute fake IDs to activate user commands.

The full replacement still needs complete game/mode/sprite/audio/UI integration,
Carrier/Drone lifecycle and remaining source parity, connected real optimizer,
trajectory/checkpoint pipeline, distributed learning, evaluation/experiment
orchestration and measured performance. Upstream's model reload and evaluation
work is integrated infrastructure, not proof of a complete native learner.
