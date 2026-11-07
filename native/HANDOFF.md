# Unified native rewrite handoff — 2026-11-07

Worktree `B:\Bees\unified-native`, branch `rewrite/unified-native`.
Fetched upstream before this increment; `ca8fb367e` remains incorporated by
`ed97aa136`. Local `fcabe9599` connected player movement and reservation cleanup.
Earlier rendering, collision/support, specials/Beacon and navigation work remains
integrated with upstream group identity, model switching and evaluation reporting.
Backup branch `backup/native-before-upstream-20261107` and pre-merge `stash@{0}`
remain safety copies; do not reapply the old WIP over finished code.
The original `B:\Bees\Assets` checkout and live training were not changed.

## Latest implemented increment

- Native selection now owns exact SquadLifetime values, scoped to world/episode.
  Left-click selects a player squad; Ctrl-click adds without duplicates; a box
  selects all source-authorized squads touched by real primary ship colliders.
  Rendering highlights every selected member. Selection survives member losses,
  prunes retired lifetimes and clears on world/episode changes.
- The read-only box query constructs one collider and reuses canonical physics
  overlap against cached hulls; there are no copied entities or center/AABB-only
  substitutes. Render views borrow the real roster and object-safe permission
  provider. Physics contact events and policy observations are unaffected.
- Typed batch moves carry world/episode and complete selected squad lifetimes.
  The host deduplicates in source selection order, preflights eligible squads,
  skips retired/locked/non-player owners, and executes existing finalization,
  pending-AI retirement, formation and navigation. It allocates no fake outcomes.
- Existing schema 19 source data supplies permissions, offsets and clearances;
  no additional schema change. Source UI Hold calls StopChasing; it must not be
  mapped to the AI Hold command when continuing command integration.

## Executed evidence and limits

Windows GNU toolchain: `B:\Bees\Runtime\Toolchains\rust-native`.
Set its rustup/cargo homes; add cargo/bin and w64devkit/bin to PATH;
RUSTUP_TOOLCHAIN=stable-x86_64-pc-windows-gnu,
RUSTFLAGS=-C link-self-contained=yes. System PATH was not modified.

- All-target/all-feature compilation, headless compilation and 106 passing
  existing simulation tests are recorded
  in `B:\Bees\native-capability-qualification\workspace-selection.txt`.
  Six asset catalogs match Unity sources; diff whitespace checks passed.
- External disposable `src\bin\player_batch.rs` in that qualification folder uses
  the real schema-19 importer, group identities, host and canonical fixed steps.
  `player-batch.txt`: collider-edge selection succeeds with ship center outside;
  Ctrl/box selection deduplicates; two player squads move, duplicate owner runs
  once, enemy and stale lifetimes are skipped. Both arrive after 668 ticks, with
  two path jobs. `player-batch-locked.txt`: one locked selected squad is skipped
  and remains at its original position; the other arrives after 668 ticks with
  one path job. These are supplied fixture actions/identities, not trained
  inference, authentic Unity exports or real server reservations.
- Earlier `player-host-latest.txt` checks supersession preserving outcome IDs
  8001/8002 and late strategy discard retaining IDs 9001/9002.
- Actual mouse interaction remains unverified: the restricted launch exposed no
  target window; desktop launch exposed the correct live fixture window, but
  Computer Use app approval timed out before inspection/input. Both isolated
  fixtures were stopped by verified executable path/PID; no fixture remains live.
- C# export compilation/execution, authentic model/reload/evaluation runs,
  Unity/Box2D parity, Linux execution and multi-machine operation are unverified.

## Next work / full objective remains incomplete

Exercise mouse drag/Ctrl selection and multi-ship movement on authentic schema-19
Levels with real policies and source Stage palette/collision matrix. Native ship
inspection/right-click movement does not yet implement source enemy attack clicks.
Complete player/scripted command origin and reservation ownership within the
existing director before exposing attack/guard/mining/healing/chase controls;
never fabricate server selections or IDs to activate local commands.

The full replacement still needs complete game/mode/sprite/audio/UI integration,
Carrier/Drone lifecycle and remaining parity, a real connected optimizer and
trajectory/checkpoint pipeline, distributed learning, evaluation/experiment
orchestration and measured performance. Keep one authoritative gameplay system.
