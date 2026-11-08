# Unified native rewrite handoff - 2026-11-08

Worktree `B:\Bees\unified-native`, branch `rewrite/unified-native`.
Read the attached goal and root AGENTS.md before continuing. The complete Unity
replacement remains incomplete. Original `B:\Bees\Assets` and live training
were untouched. Work is local; no push or unrelated experiment-queue merge.

## Merge and ownership

Merged `origin/rewrite/native-outcome-recovery-20261108` at `eb152d3af`, including
primary rewrite head `a020eb77e`, into local `53c51f639`. Both remote refs were
fetched again before finalizing. Resolve future work against the current HEAD.

The incoming matchup selector is the single 40-way ranking implementation.
The local targeting adapter supplies real Level context/combat timers and installs
one selected enemy; no preview path rerolls Random. Ordered learned ship sightings,
source tie ordering, first allied enemy for GangUp, and metric-specific metadata
requirements are preserved. New targeting retires older pending strategy authority.
Commands capture their actual selected enemy and installed targeting reservation.
The episode-owned credit ledger aggregates strategic TSV for targeting used by
commands, including Retreat independently of shooting eligibility. Unused or failed
episode reservations discard. Delayed projectile credit remains episode-owned.
Schema25 exports real source targeting context and combat-activity timers; missing
state is not guessed. Newborn Beacon metadata is installed before vision.

The local off-thread NativeSocketWorker remains the gameplay socket path. Blocking
DNS/TLS/socket I/O stays outside physics; bounded queues, explicit retries, original
payloads, transient retirement and unhandled server bytes retain one owner. Incoming
socket functionality is consolidated here, without a second blocking host facade or
competing in-flight hash set. Socket counters now include actual store transmissions.
Retired replies checkpoint discards before delivery. Persistence failure poisons
transmission and retains exact unsettled reservation IDs.

One DurableHiveMindConnection owns hashes, retries, acknowledgements and both saved
journal frontiers. New schema2 journals retain random incarnation, global sequence,
SHA-256 accepted-prefix validation and terminal-boundary validation. Torn final
lines are ignored; altered accepted prefixes, truncation and invalid rows fail.
The incoming schema1 reader retains registered run/per-world progress from its
original checkpoint, including acknowledged history. It requires the expected run,
ordered terminal boundaries and complete rows; unregistered runs are refused.
Both readers use the same outcome validator and atomic connection checkpoint.
Never relabel an old journal or submit the same journal through both formats.

The policy runner accepts `recover_notifications` for stopped schema2 journals and
`recover_outcome_journals: [{"run_id": "original-run", "path": "old.jsonl"}]` for
registered schema1 journals. Both require the original existing checkpoint and
exclusive ownership. New output paths must be fresh. No interrupted world is rebuilt.
`recover_outcomes` is the standalone schema2 replay example. Network authentication,
configured recovery startup and resident-model launcher paths compile; their full
production execution remains unqualified.

## Executed against the merged source

Disposable fixtures and receipts: `B:\Bees\native-capability-qualification`.

- `merged-workspace-tests.txt`: workspace all-target/all-feature tests pass,
  including all106 existing simulation tests. `merged-headless.txt`: headless
  workspace check passes. Existing compiler warnings remain; no committed tests added.
- `merged-network.txt`: real loopback TCP/WebSocket setup, delayed/fragmented replies
  across idle timeouts while physical ticks advance; every store payload matches its
  prior disk checkpoint. Stale strategy/targeting discards persist. Accepted targeting
  installs an actual enemy and retains episode ownership. Unused selection discards
  at episode final.503 needs explicit identical-hash retry. Unacknowledged disconnect
  and reconnect preserve the terminal batch;409 retains exact stale IDs. Atomic
  checkpoint replacement failure preserves disk state and returns reservation701.
- `merged-recovery-crash.txt` / `merged-recovery-readback.txt`: actual native episode
  with first-sighting TSV3 exits73 after journal fsync, before connection checkpoint.
  Recovery imports the missing batch once, retains hash/payload across restart/503,
  and preserves200/409 acknowledgement history. Torn rows, prefix edits, duplicates,
  truncation, absent checkpoint and earlier checkpoint loading are exercised.
- `merged-legacy.txt`: original-format checkpoint with registered schema1 run replays
  a terminal batch, preserves exact retries and acknowledged frontier on reopen.
  Both saved frontier formats coexist without clearing acknowledgement history.
  Wrong/unregistered run IDs and detached terminal outcomes are rejected.

These are supplied executable fixtures and loopback/synthetic acknowledgements,
not authentic Unity exports or production Steam/WSS/database receipts. Targeting
installation/unused settlement ran end-to-end; all40 selections, combat activity
parity and earned targeting TSV have not yet been qualified end-to-end. Unity C#
compilation/export, Linux execution, power-loss durability, physical window timing,
real model training and distributed performance gains remain unverified.

## Continue here

Finish ordinary-game targeting request cadence, authored bans and source-correct
retargeting through the existing connection owner. Qualify earned targeting credit
and schema25 activity from real exports. Then close asteroid/MapObject targeting,
authored dynamic factories and scripted Carrier/Drone lifecycle.

The shared playable/headless/policy Level already uses canonical CombatWorld,
roster, commands, navigation, physical weapons and episode outcomes. All5 ranged
HiveMind commands, capability redirects, player input, sighting and Fire Barge
credit were integrated before this merge; prior executable receipts remain in the
qualification directory but are not new proof for changed paths.

Full replacement still needs ordinary game launcher/modes, finished presentation/
sprites/animation/audio/input, authoritative persistence, connected optimizer/
trajectory/checkpoints, distributed learning, evaluation and experiment scheduling,
Windows/Linux execution and measured speed improvements. Keep one authoritative
gameplay implementation across client, headless simulation, policies and evaluation.
