# Unified native rewrite handoff - 2026-11-09

Worktree `B:\Bees\unified-native`, branch `rewrite/unified-native`.
Read the full goal and fresh root AGENTS.md before further work. The complete
Unity replacement remains incomplete. The latest Experiment Hub and queue
work has been merged locally.

## 2026-11-10 asteroid fallback in gameplay

Ordinary turrets use ordered asteroid proximity contacts for fallback and
wait for map entry. The tactical view shows the active asteroid aim point,
and automatic-shot telemetry counts these projectiles. Windows fixtures
exercised ordinary, Beam Cannon, and Laser Builder firing through the
native host. Each landed a four-point hit after map entry. The Beam Cannon
and Laser Builder fixtures also checked the rendered aim marker and shot
count. MapObject targeting and a matched Unity comparison remain open.

## 2026-11-10 Windows collection and learner dispatch

The isolated Experiment Hub registry at
`B:\Bees\native-capability-qualification\hub-dispatch-collect-b38a1c311ae1427586077d527c595c68`
automatically dispatched two jobs through authenticated loopback worker
heartbeats and signed Windows owner receipts. The collector persisted
36 policy rows in Hub progress and released capacity. A separately
queued learner consumed the collector's pinned bundle, produced a
checkpoint from 18 optimizer input steps, and released capacity.
Both terminal states survived registry reopen. The two worker identities
ran on one Windows host and shared local artifact storage. Remote
transfer and Linux dispatch remain to be qualified.

## 2026-11-10 active-grid inference qualification

Model timing now samples actual inference calls. A Windows fixture with 64
ships, four asteroid hulls, and two slots served by the same ONNX policy
sampled 24 of 120 calls and 1,536 of 7,680 decisions with both periods set
to five. The old sampler recorded zero calls in the matched configuration.
Contiguous slot groups now borrow observation and mask slices. Direct ONNX
checks matched actions for contiguous and interleaved slots, and the
rollout matched seven journal records after normalizing run identifiers.
All 106 native tests passed. Short paired release timings overlapped.
The fixture had no weapon hits and used a configured scene, so authored
Unity scenario parity and a durable throughput gain remain unverified.

## 2026-11-10 empty obstacle broadphase

Physics skips queries against an empty grid and the per-ship obstacle scan
when there are no obstacle entries. Contact exit reconciliation still runs.
In matched Windows release movement fixtures across 16 arenas, serial
throughput rose from 88–95k to 138k world steps/s at 64 ships per arena,
and from 23–24k to 34–35k at 256 ships. These fixtures have no obstacles,
weapons, or inference. A separate before/after native ONNX rollout matched
all ten journal records for an authored asteroid collision and elimination.
All 106 native tests passed. Large battles with active collision grids
and neural inference still need measured runs.

## 2026-11-10 verified round throughput metrics

The native round now checks the simulator's structured summary for the
expected run ID, manifest, episode count and captured rows before launching
the optimizer. The round receipt and Hub progress include rollout phase
timing and work counts separately from optimizer metrics.

Two automatic Windows rounds in
`B:\Bees\native-capability-qualification\hub-round-metrics-4a3a6ddd293d458396f128412636cd60`
each recorded 62 world steps, 36 policy rows, and 18 optimizer input rows.
Startup dominated the wall time of this short fixture. A rollout cut off
after one round was refused before optimizer launch. Larger-battle and
Linux performance remain to be measured.

## 2026-11-10 automatic Windows checkpoint pipeline

The isolated Windows Hub at
`B:\Bees\native-capability-qualification\hub-automatic-bees-5b45fa18799b4baba7e931e6bdb95678`
automatically claimed, started and released two native rounds after one
explicit arm. Both Job Object terminal receipts verified. The successor
was offered only after the predecessor released capacity and used its
verified checkpoint and manifest. The rounds supplied 36 learner decisions.
Both completions survived coordinator reopen. The harness's final disarm
call omitted its confirmation flag and was refused. Reopening confirmed
automation was disabled. Linux and multi-machine training remain open.

## 2026-11-10 supervised Windows native training

The Windows Hub owner ran two actual CPU training rounds in an isolated
coordinator and worker at
`B:\Bees\native-capability-qualification\hub-owned-final-7fbb54593d34400096a78fa279c8f5b5`.
Both jobs returned signed terminal receipts, verified their checkpoints,
released capacity, and remained finished after reopening the coordinator.
The successor consumed the predecessor checkpoint recorded in its Hub order.

The first supervised attempt exposed an ML-Agents CPU selection bug on a
GPU-equipped host. ML-Agents changed its preferred device to CPU while
PyTorch still defaulted implicit critic tensors to CUDA. The native learner
now resets that default explicitly. The Hub launches rounds with separate
rollout and optimizer diagnostics, including on Windows without a console.
This qualification used an offline framework-dependent .NET 10 owner build
and manually authorized starts and releases. Linux and automatic scheduling
of this training adapter remain unqualified.

## 2026-11-10 native Bees checkpoint pipelines

The Hub can queue native Bees successors on the same worker. A successor
requires the authenticated predecessor checkpoint fingerprint and uses the
checkpoint and manifest verified against the predecessor's round receipt.

The isolated Windows fixture at
`B:\Bees\native-capability-qualification\hub-bees-chain-B7ekfj`
queued two stages, passed the Windows launch validator for both, ran two
real rounds, and verified the second receipt used the first checkpoint.
An incorrect predecessor fingerprint was refused. The native Windows owner
build could not access the host's NuGet configuration, so signed ownership
and capacity release through the live worker remain unqualified.

## 2026-11-10 native round Experiment Hub adapter

The machine-local `bees_native` adapter launches one model-only native
training round and maps its completion receipt to Hub progress. Its frozen
configuration digest covers the run template, trainer config, input checkpoint,
model manifest and learner slot.

The Windows qualification at
`B:\Bees\native-capability-qualification\hub-native-round-L5WEwb`
passed the Windows supervisor's invocation validator, ran the real child
command and read Hub progress for 18 learner decisions and two episodes.
Native supervisor ownership, Linux execution and automated continuation
still need end-to-end qualification.

## 2026-11-10 historical opponent and capture aggregation

The native learner can now combine completed captures of one behavior
checkpoint and train only on its explicit manifest slot when an opponent
uses different weights. The slot follows the checkpoint across the
existing faction swap. Journal identity scopes trajectories and prevents
duplicate episode submission.

The Windows fixture at
`B:\Bees\native-capability-qualification\historical-poca-e5c2800587ff4b85b1eb1e7315f3faaf`
combined two captures and four episodes. The learner accepted 36 decisions
and excluded 36 opponent decisions. Adam advanced six minibatches.
Wrong slots and duplicate episodes were refused. This path consumes
completed files locally. Live worker transport remains open.

## 2026-11-10 individual controller retirement

An opt-in policy capture now records a death-tick observation boundary
while other controllers continue. The learner closes the dead controller's
trajectory, bootstraps from surviving teammates, and removes the retired
controller from subsequent group observations. Boundary observations use
a copy of the live policy's identity mapping.

The Windows receipt at
`B:\Bees\native-capability-qualification\survivor-final-b050d7452d1b438ab7010ecb3a69c615`
verified retirement at tick 1, a same-tick survivor bootstrap observation,
and episode completion at tick 25. MA-POCA used 51 transitions and completed
17 minibatches. Controller rebinding and asynchronous decision clocks
remain outside the capture learner's supported cohort.

## 2026-11-10 elimination trajectories

The combat death record carries the retired policy binding revision into the
native journal. On same-tick side elimination, final capture includes the
retired controller's inactive faction observation so its group terminal
reward reaches MA-POCA.

The Windows fixture at
`B:\Bees\native-capability-qualification\elimination-full-matrix-a9eedb5913de40b39984775bf33c7a88`
verified an asteroid death on tick 1, three terminal transitions, and one
optimizer minibatch. In the paired run with a surviving ship on the losing
controller's side, the death was journaled on tick 1 and the episode
continued until tick 25. The learner rejected that capture pending support
for individual retirement and survivor bootstrapping.

## 2026-11-10 native MA-POCA terminal update

The completed-world capture now encodes final observations from the shared
gameplay state. The CPU learner uses them with the journal's terminal group
rewards and the existing MA-POCA trajectory and optimizer code. The Windows
receipt at
`B:\Bees\native-capability-qualification\terminal-poca-a65d4300578a44d9a42bc5b315a0a5d1`
contains 180 decisions and three terminal transitions with the expected
group rewards. The update completed five minibatches. The preceding
checkpoint round trip independently verified that Adam resumed at step 5
and reached step 10.

The learner currently requires a stable, synchronized group roster and
one behavior checkpoint across all slots. Individual death and rebinding
observations are the next trajectory boundary to implement.

## 2026-11-10 ML-Agents behavior sampling

Native rollout can now load the structured v26 trainer actor from a
checkpoint, apply its clipped environment actions, and capture raw samples
and behavior log probabilities separately. The Windows receipt at
`B:\Bees\native-capability-qualification\behavior-sampling-da68c9a24cae46a2826b575c9adc406c`
contains 1,200 decisions across both sides from a freshly initialized actor.
The trainer reproduced the recorded log probabilities within 0.000001.
The remaining learner work starts at groupmate experience and terminal
trajectory assembly, followed by the MA-POCA optimizer handoff.

## 2026-11-10 applied native decisions

The rollout window delivers validated decision rows after successful world
dispatch and before reward delivery. Optional binary capture pairs those rows
with the existing notification journal. The Windows receipt in
`B:\Bees\native-capability-qualification\applied-decisions-427b22a1352543a3942e5404516e2f8b`
contains 600 decisions for controller 1003 and its terminal at tick 3000.
The reader refused the run stopped at 20 rounds and an unrelated journal.

The capture verifies the simulator's decision boundary. Optimizer integration
still needs online behavior-policy sampling with log probabilities, live
groupmate data, and the source trajectory owner's terminal handling.

## 2026-11-10 trainer ONNX evaluation

The Windows receipts in
`B:\Bees\native-capability-qualification\trainer-onnx-final-d8619b9dabb444c7b2fe4615f4dc875b`
use a freshly initialized structured v26 actor exported by the installed
ML-Agents ModelSerializer. The resident ONNX worker ran the deterministic
action outputs through four native episodes and validated 4,800 policy rows.
Swapped observation and mask inputs failed during worker startup.
ONNX slots can cap intra-op threads for bounded CPU placements.

The next training integration needs to pass native decision and terminal data
to the existing MA-POCA trajectory owner, including real sampled action
log probabilities and groupmate data. The frozen ONNX path supplies actions
for evaluation and does not produce optimizer inputs.

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
