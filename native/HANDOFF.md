# Unified native rewrite handoff — 2026-11-07

Worktree `B:\Bees\unified-native`, branch `rewrite/unified-native`.
Upstream `ca8fb367e` remains incorporated; fetched before this increment.
Prior local origins/settlement work is in `aeb1bc9f7`; selection/movement in
`fcabe9599` / `8d76006dd`. Rendering, capabilities, navigation and source schema19
remain integrated with upstream model switching and evaluation infrastructure.
Original `B:\Bees\Assets` checkout and live training were not changed.

## Latest integrated work

- NativeLocalCommandAction replaces unparameterized local command kinds. It
  carries Guard's exact SquadLifetime, Patrol's actual corners, Mining's asteroid,
  ordered Beehive IDs for Heal, or FullRetreat's WarpGate. The existing common
  executors retain those choices; no nearest/random AI fallback is substituted.
- HiveMind entrypoints retain their existing target selection. Local Guard,
  Patrol, Mining and Heal omit AI-only timeouts, matching the source IsHiveMind
  conditions. FullRetreat and scripted Hold keep their source timeout behavior.
- NativeLevelEventProcessor finalizes the old local command before attempting its
  replacement, including unavailable chosen targets. Outcomes remain in the one
  outbox, and pending AI authority retires through the existing ingress boundary.
  The shared finalization helper is now finalize_local_command.
- NativeSquadRoster owns actual registration order (including minion append and
  retirement). Friendly snapshots read that order and the canonical command
  director's current kind/guard target. Metadata no longer supplies duplicated
  command registries or independent squad order. The collect_live_friendlies
  trait method now receives the real director; defaults work for imported Levels.
- All movement, healing slots, mining credits, warp participants/animation,
  damage, cooldowns and completion remain in the same authoritative simulation.

## Executed evidence and limits

Windows GNU toolchain: `B:\Bees\Runtime\Toolchains\rust-native`.
Set rustup/cargo homes there; put cargo/bin and w64devkit/bin on PATH;
RUSTUP_TOOLCHAIN=stable-x86_64-pc-windows-gnu,
RUSTFLAGS=-C link-self-contained=yes. System PATH was not modified.

- Workspace all-target/all-feature compilation, 106 existing simulation tests,
  headless compilation, six asset verifiers and diff whitespace checks passed.
  Test evidence: `B:\Bees\native-capability-qualification\workspace-chosen.txt`.
- External disposable `src\bin\chosen_commands.rs` in that qualification folder
  exercises actual schema19 import, builder and NativeLevelRules. Evidence:
  `chosen-commands.txt`. Chosen asteroid 6 is mined while nearer asteroid 5 stays
  untouched; healing reserves hive 6 before nearer hive 2 in the supplied order;
  Guard honors farther owner14 and survives beyond its tiny AI timeout; Patrol
  moves to the supplied top-right corner and remains active; FullRetreat really
  warps through chosen gate6 at tick202, despite gate4 being closer initially.
  A missing mining target finalizes the prior Patrol and does not pick another
  asteroid. A deliberately non-numeric source squad registration order is kept.
- `local-commands-chosen.txt` re-executes origin/settlement/late-AI retirement and
  local cancellation. Player Hold stays unsupported because source UI Hold ends
  chasing. These are supplied scenarios/actions/identities, not authentic Unity
  exports, trained inference, actual server reservations or learning evidence.
- Actual mouse interaction, C# export execution, authentic model/reload/evaluation
  runs, Unity/Box2D parity, Linux execution and multi-machine behavior remain
  unverified. Previous mouse fixture stopped at Computer Use app approval timeout.

## Highest-value next work / full objective remains incomplete

Wire host-scoped player action requests using actual player permissions, source
shooting strategy and source command settings. Export/retain those source inputs;
never invent defaults. Then connect selected-target support/attack UI and complete
scripted queue progression. Ordinary non-policy weapon targeting/firing also
requires integration. Source UI Hold calls StopChasing, not the AI Hold command.

The full Unity replacement still requires complete game/mode/sprite/audio/UI,
Carrier/Drone lifecycle and remaining parity, a real connected optimizer and
trajectory/checkpoint pipeline, distributed learning, evaluation/experiments and
measured performance. Keep one authoritative gameplay implementation throughout.
