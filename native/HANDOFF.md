# Unified native rewrite handoff — 2026-11-07

Worktree: `B:\Bees\unified-native`, branch `rewrite/unified-native`.
Fetched `origin/rewrite/unified-native` during this increment: remote remained
`a31ddf669`, already an ancestor of local `d77f1c48b`. No upstream rewrite
changes were missing. The original `B:\Bees\Assets` checkout and training
processes were not switched, stopped or reconfigured.

## Current integrated work

The previous local commit adds an optional tactical window, camera/inspection,
pause/single-step, source-world rendering and frame captures to `policy_rollout`.
The headless and viewed paths share `NativeHostRuntime`; this is still a
presentation development tool, not the finished playable game.

This increment connects source ship setup to that same runtime:

- Physical scene schema **16** exports/imports actual `Ship.SizeClass`, configured
  pixel sizes and factions, plus active Beehive/WarpGate child colliders. The
  exporter shares box/polygon/circle transformation code with MapObjects. Old
  exports need a fresh export; there is no guessed schema-15 compatibility path.
- The shared builder installs original-health collision profiles, support zones
  and policy mining/healing/warp eligibility. Source flags are cross-checked;
  readiness and cooldowns remain owned by CombatWorld.
- New ship collider caches are initialized at construction. A direct run first
  reproduced all contact-based initial masks being false; initializing the
  actual geometry enables correct tick-zero masks without advancing physics.
- Negative FleetShip IDs from `Level.RandomSquadSetup`, campaign and transient
  allocation are retained through import, combat ownership and delayed-shot
  validation. Persistence remains a separate value. The negative-ID fixture
  initially failed startup with `InvalidPolicyMining`; the same fixture now runs.
- Scout setup and minion-roster validation now share the catalog's Beacon type
  **1**. Previously both hardcoded 14 (Honeybee), rejecting the real Beacon
  asset. The actual type-1 spawn and squad registration now execute successfully.

## Current executed evidence

Windows GNU Rust 1.99.1 toolchain: `B:\Bees\Runtime\Toolchains\rust-native`.
Set RUSTUP_HOME to its `rustup`, CARGO_HOME to its `cargo`; add `cargo\bin` and
`w64devkit\bin` to PATH; set RUSTUP_TOOLCHAIN=stable-x86_64-pc-windows-gnu and
RUSTFLAGS=-C link-self-contained=yes. No system PATH change was made.

- `node native/tools/verify-native.mjs`: six source asset checks and existing
  executable Rust checks passed.
- All-feature/all-target workspace build and tests passed: **106 existing
  simulation tests**. The other crates currently have no enabled tests; their
  compilation alone is not runtime evidence.
- Disposable explicit scene fixtures: `B:\Bees\native-capability-qualification`.
  These exercise the actual schema-16 importer -> builder -> shared Level step,
  with deliberately supplied actions and no learner/model substitutes:
  `verification.txt` / `negative-after.txt` show 5 minerals credited to fleet,
  50 HP restored, a Human ship warped, and live cooldown masks, over three ticks.
  `asteroid-verification.txt` shows an authored asteroid contact reducing ship
  health 50 -> 0 without a missing-profile failure.
  `scout-before.txt` / `scout-after.txt` reproduce the rejected actual Beacon and
  then show a type-1 Beacon inheriting negative fleet identity and joining the
  shared squad roster. Other supported bomber/charge registrations also accept
  signed transient identities. These are explicit contract fixtures, **not**
  authentic Unity scene exports or physics/gameplay parity evidence.

## Remaining / highest-value next work

The full replacement goal is **not complete**. Compile/run the updated Unity
exporter and launch an authentic fresh schema-16 battle with its real Stage,
collision matrix and v26 model in both headless and viewed modes. Confirm initial
capability masks, exact resource/health/warp events and cancellation/terminal
outbox settlement. Unity export execution and end-to-end model-host behavior
remain unverified here. No Linux distribution is installed in WSL; Linux builds
and execution remain unverified. No speedup or cross-engine parity is claimed.

Supported ship-special setup still needs complete source import integration
(Scout Beacon specs, carrier relations, bomb/charge powers and lifecycle state),
including metadata for spawned minions. Normal user orders, game modes,
sprites/audio/UI, actual optimizer/trajectory/checkpoint plumbing, distributed
learning and experiment orchestration remain unfinished. Preserve canonical
world/Level/connection ownership rather than adding another simulator or
training on partial/invented trajectories.
