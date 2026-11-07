# Unified native rewrite handoff — 2026-11-07

Worktree: `B:\Bees\unified-native`, branch `rewrite/unified-native`.
Fetched the branch before this increment: upstream remains `a31ddf669`, with
local commits `d77f1c48b` and `54a74dc68` already applied. The separate original
`B:\Bees\Assets` checkout and live training processes were left unchanged.

## Implemented

The shared scene -> native Level path now installs all five already-ported ship
specials, in addition to the earlier collision, mining, healing and warp setup.

- Source scene schema **17** carries actual bomb/charge power, optional Striker
  carrier identity and Scout Beacon prefab/Create/Setup inputs. Fresh-scene
  exports reject consumed bomb, charge and Scout resource/cooldown histories.
  New exports are required; do not upgrade an older file by inventing fields.
- Special registration happens after all ships exist, so forward carrier
  references work. Non-policy Fire Barge death explosions and game Scout/Striker
  setup use the same core methods as policy ships. Barge/YellowJacket game-command
  activation still obtains its live power from the existing command context.
- Policy Strikers may have no Carrier, matching ordinary source Strikers. Their
  observation parent uses the core's current carrier relation, including death
  reassignment, instead of retaining the initial imported parent forever.
- Scout Beacon templates are shared immutable inputs. Physical spawns install
  real original-health/size collision profiles. The Level registers transient
  fleet, perception and vision data before downstream consumers, and the existing
  event pass registers the real minion Squad. Both policy-tick and later timed
  command spawns use this lifecycle. Custom metadata without a spawn adapter fails
  explicitly. Minions acquire no fabricated policy controller/model route.
- Corrected the reproduced Beacon heading mismatch: source Pool/Setup restores
  zero rotation; it does not copy the mother's rotation. A rotated-Scout check
  failed with native heading 90 before correction and succeeds with heading 0.
- Multi-arena policy startup/reset reuses the Stage ship catalog during source
  parsing; it does not reparse a separate immutable hull catalog for every arena.

## Executed evidence

Toolchain outside the checkout: `B:\Bees\Runtime\Toolchains\rust-native`.
Use its rustup and cargo homes; add `cargo\bin` and `w64devkit\bin` to PATH;
RUSTUP_TOOLCHAIN=stable-x86_64-pc-windows-gnu,
RUSTFLAGS=-C link-self-contained=yes. System PATH was not modified.

- Workspace all-target/all-feature compilation and 106 existing simulation tests
  passed on Windows GNU Rust. Other crates currently have no enabled tests;
  compilation is not evidence of their runtime lifecycle behavior.
- Six authored asset catalog checks passed.
- Disposable explicit contract fixtures in `B:\Bees\native-capability-qualification`
  exercise the actual schema-17 importer, builder and NativeLevelRules:
  `specials-import.txt` executes all five specials. Carrier-backed and standalone
  Strikers each drop a real bomb and deal delayed damage after 111 fixed ticks.
  Fire Barge/Yellow Jacket self-death and Barge charge-start events are observed.
  `scout-import.txt` verifies a real Beacon with negative inherited fleet ID,
  immediate metadata/roster registration, inclusion in a complete ally perception
  snapshot and a successful following Level step. `scout-cap.txt` verifies the
  source side cap masks the special before a spawn. `beacon-heading-before.txt`
  records the prior heading mismatch. These are deliberately supplied actions
  and explicit scenarios, not trained inference or authentic Unity scene exports.

## Remaining / next work

The original replacement goal is **not complete**. Execute the updated C#
exporter in Unity and use an authentic schema-17 Level, Stage palette, collision
matrix and actual v26 model in both viewed and headless modes. Confirm fresh
special masks, Beacon visibility/reward attribution, carrier retirement and
outbox settlement. C# export execution, live model-host operation and actual
multi-machine behavior remain unverified. Linux execution remains unverified;
no installed WSL distribution was found in the prior environment check.

The tactical window still needs normal user commands and complete game/mode,
sprite/audio/UI integration. Full Carrier/Drone spawn lifecycle and remaining
source parity require work. A connected real optimizer/trajectory/checkpoint
pipeline, distributed learner, evaluation and experiment orchestration are still
unfinished. No Unity/Box2D parity, production durability or speedup claim has been
established. Keep the same authoritative gameplay implementation throughout;
do not train on partial or invented trajectories to conceal missing integration.
