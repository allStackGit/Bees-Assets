# Unified native rewrite handoff — 2026-11-07

Worktree `B:\Bees\unified-native`, branch `rewrite/unified-native`.
Latest upstream `ca8fb367e` incorporated; fresh fetch before this increment found
no new upstream commits. Original `B:\Bees\Assets` and live training untouched.
Prior source-target command work: `34881ba6b`; origins: `aeb1bc9f7`;
player selection/movement: `fcabe9599` / `8d76006dd`.

## Latest integrated work

- Scene schema20 exports loaded command timings/distances and each actual
  Squad.GetShootingStrategy name. Import validates registry identity/settings;
  metadata retains them under the real squad lifetime. Older scenes need export.
- NativeHostRuntime::player_command routes chosen Guard, Patrol, Mining, Heal
  and FullRetreat through canonical Level commands. World/revision, live squad,
  permissions, input lock, policy ownership, formation, geometry, strategy and
  settings preflight precedes pending HiveMind request retirement. The token is
  revalidated synchronously before execution; failures use normal Level abort.
- Movement and support now share one private squad-authority preflight. Support
  adds its command-specific inputs without inventing a movement destination or
  server reservation. The existing director/outbox owns replacement and outcomes.
- Host attack inputs are rejected pending actual enemy/weapon integration.
  Player Hold remains rejected: source UI Hold calls StopChasing, not AI Hold.
  Existing chosen-target execution, local identity, AI-only timeout differences,
  canonical roster order, navigation, model switching and evaluation remain.

## Executed evidence and limits

Windows GNU toolchain: `B:\Bees\Runtime\Toolchains\rust-native`.
Set rustup/cargo homes there; cargo/bin and w64devkit/bin on PATH;
RUSTUP_TOOLCHAIN=stable-x86_64-pc-windows-gnu,
RUSTFLAGS=-C link-self-contained=yes. System PATH unchanged.

External qualification folder: `B:\Bees\native-capability-qualification`.
`player-support.txt` / `src/bin/player_support.rs` exercise imported schema20,
builder, NativeHostRuntime and actual run_window for all five support commands.
Chosen asteroid6 is mined while nearer5 is untouched; Heal reserves6 before2;
Guard picks farther squad14; Patrol follows supplied corners; retreat warps
through chosen gate6 at tick202. Stale episode, missing owner and UI Hold reject.
Late strategy reply9001/9002 is discarded for each; local Patrol finalization
retains imported Closest shooting and Player origin. Missing target finalizes
prior command without fallback. These use supplied fixtures/actions/identities
and an in-memory sink, not trained inference or actual server reservations.

Workspace all-target/all-feature tests, headless check, assets and movement
qualification are recorded with this increment. Existing simulation suite has
106 tests; other crates have no enabled tests. Compilation does not prove parity.

Actual mouse interaction, C# export execution, authentic model/reload/evaluation
runs, Unity/Box2D parity, Linux execution and multi-machine behavior unverified.
Previous mouse fixture stopped at Computer Use app approval timeout.

## Highest-value next work / full objective remains incomplete

Connect window support actions and multi-squad admission to the new host API;
complete source attack inputs, ordinary non-policy weapon targeting/firing,
Chase/StopChasing and scripted queue progression. PolicyOnlyMetadata deliberately
still rejects HiveMind strategy activation: complete its source strategy and
per-command snapshots before enabling that path.

The full Unity replacement requires complete game/mode/sprite/audio/UI,
Carrier/Drone lifecycle and remaining parity, real connected optimizer and
trajectory/checkpoint pipeline, distributed learning, evaluation/experiments and
measured performance. Keep one authoritative gameplay implementation throughout.
