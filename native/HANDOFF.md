# Unified native rewrite handoff - 2026-11-09

Worktree `B:\Bees\unified-native`, branch `rewrite/unified-native`.
Read the full goal and fresh root AGENTS.md before further work. The complete
Unity replacement remains incomplete. The latest Experiment Hub and queue
work has been merged locally.

## 2026-11-10 resident policy evaluation

Terminal evaluation journals now record authored policy slots by side
separately from MA-POCA reward-group assignments. The fixed-opponent
comparator verifies that both sides have distinct authored policy slots.

The Windows receipts in
`B:\Bees\native-capability-qualification\resident-seats-2f06fd33f3324d5cb7c1b672022f2641`
use deterministic TorchScript fixtures. The single-controller run processed
6,000 rows across two episodes and changed manifests at the episode boundary.
The two-controller reference and candidate processed 6,000 rows each and
matched one episode with a fixed opposing artifact. The comparator rejected
the single-controller journal for fixed-opponent model comparison.
Learned-checkpoint evaluation and optimizer integration remain open.

## 2026-11-10 launcher integration

The local rewrite branch now combines the upstream source command scheduler
with model-free launching, explicit replay seeds, evaluation journals and
comparison, whole-run timing, and confirmed WebSocket shutdown. The launcher
calls source pulses only at gameplay step boundaries and caps headless windows
at the next due pulse. Type-specific targeting uses the physical ship type
table shared with request bans.

The Windows launcher receipt in
`B:\Bees\native-capability-qualification\primary-integration-44c1a08c5d684f8eb2feee1e9c6875a8`
covers two model-free episodes with seeds 123 and 987, commands on fresh squad
lifetimes 10 and 100, three acknowledged store messages, and a confirmed close.
Production HiveMind, Linux runtime, and Unity export execution remain
unverified. The larger replacement and learner work remain open.

## 2026-11-09 continuation

## 2026-11-09 command-cycle integration

A source-owned native HiveMind command cycle has been added WITHOUT another
gameplay/world/command queue. HiveMindSession exposes read-only live pending
requests and original targeting OpponentId. NativeSocketWorker transmits exact
pending targeting and strategy payloads once per authenticated socket, with
bounded backpressure and explicit same-hash retry. The opt-in host
`poll_network_and_continue_source_commands` consumes validated targeting
receipts and immediately creates source-built strategy requests using the
ORIGINAL targeting opponent, while native-level command activation and
StoreCommands still use their existing authoritative paths.

Fresh Unity physical-scene schema27 exports Stage.ActivateHiveMind,
Level.StartingPositions, and each Squad's HiveMind-controlled/immobile flags
plus exact decimal ulong OpponentId. The importer requires these for schema27
and keeps older scene parsing, but older exports cannot silently enable new
autonomous command issuance. Policy-only metadata is an immutable authorization
adapter, NOT another roster.

`NativeHostRuntime.issue_due_source_targeting` is called on the Stage's
existing quarter-second command timer after InitialCommandDelay; the caller
supplies each Level's real (setup accepted && not paused) predicate. It
checks the single live roster/command director and the socket's pending
hashes before issuing requests. Out-of-bounds squads with no current
destination use the source side's imported StartingPosition and the SAME
native formation planner, A* navigation and CombatWorld movement controller.
A reused per-pulse formation buffer avoids per-squad temporary allocation.
Newborn non-authored Beacon minions are conservatively excluded; other
unknown squad authorities are surfaced, never guessed.

**This is an uncompiled integration milestone**, not full playable/native
launch parity. No Rust/Unity execution or performance measurements were
possible from this GitHub-only environment. The rendered/headless owning
Stage still needs to invoke its timer pulse and opt-in socket pump; actual
server setup/status must be supplied per Level. Dynamic factory/child squad
authorization, real exports across schema27, live WSS/Steam/SQL, time-domain
timer parity and high-density performance remain unverified.

## 2026-11-09 strategy-matchup continuation

The native gameplay crate now prepares source-shaped strategic and request-local
shooting matchup keys from the SAME live CombatWorld, canonical squad roster,
per-side retained HiveMind discoveries, immutable source ship letters, and
authored sprite bounds. It implements source priority of selected enemy ships
before other in-range visible enemies, first-64 ship caps, nearby ally choice,
source distance/type/ID tie ordering, reciprocal weapon range flag, immutable
Ship.OriginalHealth percentages and C# ties-to-even health buckets. Shooting
identity is deliberately acting squad + selected enemies + trailing delimiter:
nearby allies appear ONLY in the strategic key, preserving prior learning
attribution under concurrent Level requests.

The source policy-only metadata adapter uses real turret
IsShipValidTarget/RangeCollider membership, target bounds latching and world
obstacle linecast. Unsupported Bomb/Charge subclass target-validity queries
fail closed; no nominal range approximations or fabricated target observations
are introduced. A no-visible-enemy selection returns None so it is not sent
as a false strategy matchup. An empty targeting choice uses the source
no-target key and command bans.

NativeLevelBatchDriver can prepare both keys from the accepted
targeting lifecycle. Source command bans now combine the imported
composition bans, no-target bans, live ClosestFriendly occupancy, warp-gate
eligibility, shared Beehive healing capacity, and mining ship/asteroid
availability. The Unity physical exporter now also supplies the original
Level.ActivateMining flag; native strategy issuance REQUIRES this field
(legacy snapshots without it fail closed). NativeHostRuntime.request_live_strategy
now constructs both identities and every source ban without caller-built
matchups/bans and durably allocates GetStrategy through the EXISTING
DurableHiveMindConnection. The caller must still send that exact request
through the original socket worker; automatic command queue cadence,
response-to-request chaining and retargeting remain incomplete.

No Rust toolchain or runnable source scene was available in the present
execution environment. Changes were reviewed against the current Unity
source and GitHub revisions but have NOT been compiled or run. Do not use
earlier executable qualification as proof of this new path. Next: implement
the remaining dynamic-bans adapter and automatic source request/retargeting
cadence, then qualify against real Unity exports, including special weapons.

The common host can now issue `request_source_targeting`: the single live
Squad and imported authored ship-letter sources generate the first 64 sorted
ship letters, while all current enemy-side hull types determine Type A-X bans.
Unity `UserProgressData.SetShipTypes` derives that ban universe from the
fixed shooting-strategy table, so no guessed or duplicated unlock list is used.
Live owner, complete roster and source metadata are preflighted before the
durable request hash is allocated. The original socket worker sends that
request; ordinary in-bounds/AI-eligible command queue cadence is still missing.

A targeting reply rejected at ranking before game mutation durably
discards its unused reservation. Failure during installation can have
partially changed the Level credit ledger; it instead surfaces its exact
unresolved targeting ID. Likewise, an accepted strategy reply whose
activation fails surfaces both IDs, never guessing a safe discard.
The existing connection-owned StoreCommands ledger remains the only writer.
These new paths were source/cross-interface inspected but not compiled or run
in this environment. Earlier handoff runtime receipts refer to earlier code
and do not qualify these changes.

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
