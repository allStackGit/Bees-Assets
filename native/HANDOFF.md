# Unified native rewrite handoff — 2026-11-07

Worktree `B:\Bees\unified-native`, branch `rewrite/unified-native`.
Fetched upstream before this increment; `ca8fb367e` remains incorporated by
`ed97aa136`. Local `fcabe9599` and `8d76006dd` connected player movement/selection.
Previous rendering, physical capabilities, navigation and source schema 19 remain
integrated with upstream group identities/model switching/evaluation reporting.
Original `B:\Bees\Assets` checkout and live training were not changed.
Backup branch and pre-merge stash remain safety copies; do not reapply old WIP.

## Latest integrated work

- NativeCommandSelection separates execution kind/shooting from its origin:
  a full real ResolvedHiveMindCommand, or a player/scripted local identity.
  The Level owns a zero-based local sequence, scoped by world episode and squad.
  There are no fabricated server command/shooting outcome IDs.
- All origins use the existing command director, phases, formations, target
  reservations and physical CombatWorld. Server activation entrypoints remain.
  Local activation supports attacks with explicit real target/Bomb inputs, and
  scripted Hold. Other local commands remain Unsupported until their chosen
  regions/targets are supplied; AI selection is not substituted for user inputs.
- NativeLevelEventProcessor.activate_local_command validates live membership,
  allocates the local identity, retains superseded completions in its one outbox,
  and queues correlation retirement. Existing strategy/targeting ingress flushes
  those retirements before accepting replies, preserving discard obligations.
- Completion/abort notifications retain origin. FinishedNativeCommand.settle now
  returns NativeCommandSettlement::HiveMind or ::Local. Local records retain
  their value and any separately reserved targeting outcome, with no invented
  command/shooting reservations. Inspect selection.server() for real server data.
- Barge charge, Striker drop, Fire Barge queue and Yellow Jacket contact accept
  Option<i64> server attribution. None preserves ordinary unattributed combat
  events; Some must remain positive. All movement, target validation, damage,
  reservations, cooldown and death paths remain shared.

## Executed evidence and limitations

Windows GNU toolchain: `B:\Bees\Runtime\Toolchains\rust-native`.
Use its rustup/cargo homes and cargo/bin plus w64devkit/bin on PATH;
RUSTUP_TOOLCHAIN=stable-x86_64-pc-windows-gnu,
RUSTFLAGS=-C link-self-contained=yes. System PATH was not modified.

- Workspace all-target/all-feature compilation and 106 passing existing tests are recorded in
  `B:\Bees\native-capability-qualification\workspace-origins.txt`.
  Headless compilation, six asset verifiers and diff whitespace checks pass.
- Disposable external `src\bin\local_commands.rs`, evidence `local-commands.txt`:
  actual imported Level and NativeLevelRules run a scripted Hold to timeout at
  tick 3. It supersedes server reservations 8001/8002 into the outbox; late AI
  reply reservations 9001/9002 are retired; local completion retains independent
  targeting discard 9003; cancellation retains local sequence 2 in the abort
  outbox. Targetless local Guard remains unsupported without replacing gameplay.
- `src\bin\local_specials.rs`, evidence `local-specials.txt`: actual shared combat
  executes all four gameplay specials with None server attribution. Barge,
  Striker and Fire Barge damage 50->25; Yellow Jacket damages 100->75. This is a
  lower-level combat qualification, not full Level command/metadata integration.
  Fire Barge initially failed because the fixture matrix omitted explosion/ship
  contacts; an explicit fixture-only imported layer pair corrected that scenario.
- `player-host-origin.txt` re-executes the existing full host movement, command
  supersession and late-AI-discard fixture after the identity change (668 ticks).
  All these are supplied actions/scenario identities, not trained inference,
  authentic Unity exports or actual server reservations.
- Actual mouse interaction is still unverified (prior app approval timeout).
  C# exporter execution, authentic model/reload/evaluation runs, Unity/Box2D
  parity, Linux execution and multi-machine behavior are unverified.

## Highest-value next work / full objective remains incomplete

Connect local attack activation to actual host/player permissions, selected
shooting strategy and source-authored snapshot adapters before exposing enemy
right-click attack. Implement exact chosen target/region inputs for local Guard,
Patrol, Mining, Heal and FullRetreat and complete scripted queue progression.
Source UI Hold calls StopChasing, not the AI Hold command. Ordinary non-policy
weapon targeting/firing also needs full integration; movement alone is not a game.

The full Unity replacement still requires complete game/mode/sprite/audio/UI,
Carrier/Drone lifecycle and remaining parity, a real connected optimizer and
trajectory/checkpoint pipeline, distributed learning, evaluation/experiments and
measured performance. Keep one authoritative gameplay implementation throughout.
