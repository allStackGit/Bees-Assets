# Unified native rewrite handoff — 2026-11-07

Worktree: `B:\Bees\unified-native`, branch `rewrite/unified-native`, based on
`a31ddf669`. The existing `B:\Bees\Assets` training checkout was left on its
original branch; no training process was stopped or reconfigured.

## Implemented this increment

- Optional `bees-host/client` native tactical window integrated into the existing
  `policy_rollout` executable with `--view`. It borrows live shared combat bodies;
  there is no second simulation. Camera, per-world/per-episode selection,
  health/TSV inspection, pause/single-step, fixed-duration pacing, window-close
  cancellation, presentation timing, and optional exclusive `--capture` PPM output
  all connect to the existing host lifecycle. Headless execution keeps batched
  windows and does not link minifb.
- Restored compilation of the existing branch: float literals in the immutable
  sprite-size table, complete actual physics telemetry, explicit command
  transaction error type, tungstenite's actual public client API, live borrowed
  retry/reconciliation hash iterators, and integer vision-range validation.
- Reproduced WSS startup panic against a local TCP peer: tungstenite enables
  rustls without selecting a crypto provider, so ClientConfig construction
  panicked before handshake. Enabled rustls's ring provider/TLS 1.2 alongside
  its TLS 1.3 support. The same probe then returned a normal transport error
  without panicking. Certificate verification was not bypassed.
- Pinned dependency resolution in `native/Cargo.lock`. Authored generated JSON
  uses LF on every checkout. Regenerating all six catalogs showed that their
  original failures were entirely CRLF differences; geometry/data did not change.

## Executed evidence and limits

Windows x86_64 GNU, Rust 1.99.1. Local toolchain lives outside this checkout in
`B:\Bees\Runtime\Toolchains\rust-native`. Set `RUSTUP_HOME` to its `rustup`
directory, `CARGO_HOME` to its `cargo` directory, add `cargo\bin` and
`w64devkit\bin` to PATH, set `RUSTUP_TOOLCHAIN=stable-x86_64-pc-windows-gnu`,
and `RUSTFLAGS=-C link-self-contained=yes`. System PATH was not changed. MSVC
was unavailable because installed Visual Studio lacked the C++ linker.

- `cargo check --workspace --all-targets --all-features --offline`: passed.
- `cargo check --workspace --all-targets --no-default-features --offline`: passed.
- Existing simulation suite: 106 passed. Gameplay, HiveMind and host crates have
  no currently enabled tests; their compilation is not runtime qualification.
- `node native/tools/verify-native.mjs`: all six catalogs and existing tests
  passed. The verifier now includes the already-existing fragment importer.
- Disposable presentation exercise outside the repo:
  `B:\Bees\native-render-qualification`. Eight actual authored ship hulls, explicit
  collision-disabled motion fixture, 65 successful window frames and 56 canonical
  ticks. Picking and offscreen frame output exercised; initial rendering left
  tick and RNG untouched. `final.png` is a visualized copy of the actual pixel
  capture. This fixture does not validate gameplay, inference, cancellation,
  notifications, server connectivity or learning.
- `tls-before.txt` / `tls-after.txt` beside that fixture record the reproduced
  missing-provider panic and the corrected pre-handshake behavior. Actual
  authenticated WSS/Steam/server operation is still unverified.

## Still incomplete / highest-value next work

The original replacement goal is **not complete**. The policy runner's new view
and capture paths compile, but an authentic schema-15 initial Level export,
Stage palette, collision matrix and matching real v26 model have not been run
together here. Exercise that exact runner in headless and viewed modes first;
compare deterministic terminal state, ordered notification obligations and
model routing, including user cancellation. Verify Linux builds/runtime and
interactive controls on an actual display. No Unity/Box2D parity, speedup or
multi-machine durability claim is established.

The tactical view is an integration foothold, not a playable-game replacement:
sprite/assets/audio/UI, normal user orders and game modes still need native
host integration. Full source parity, actual optimizer/trajectory/checkpoint
plumbing, distributed learner and experiment orchestration remain outstanding.
Keep the existing canonical gameplay/Level/host ownership boundaries; do not
train on partial/invented trajectories to work around those gaps.
