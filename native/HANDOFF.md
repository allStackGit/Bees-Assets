# Unified native rewrite handoff - 2026-11-08

Worktree `B:\Bees\unified-native`, branch `rewrite/unified-native`.
Read the attached goal and root AGENTS.md before continuing. The complete Unity
replacement remains active and incomplete. Original `B:\Bees\Assets` and live
training were untouched. Latest work is committed locally; reconcile the current
branch/head before continuing. No unrelated experiment-queue branch was merged.

## Integrated runtime

The playable client, headless Levels and policy runner share CombatWorld, roster,
command director, navigation, physical weapons and reward/outcome notifications.
Source-authored NativePolicyOnlyMetadata supports all5 HiveMind ranged commands
(Aggressive, Circle, RightSwipe, LeftSwipe, InAndOut) through actual request/reply,
timers, movement and automatic firing. Full enemy lifetimes prevent a recycled
numeric squad ID from adopting a retired target. Real mounted contact/bounds/LOS
eligibility supplies range predicates; Ship.Firepower supplies defenselessness.

Player Attack shares Aggressive and redirects Bomber/Barge compositions through
BombingRun/Charge. Special activation validates mounted identity/power before
reservation; failed multi-bomber assignment restores reservations and world RNG.
Hold stops an Aggressive chase through normal finalization; other Hold inputs are
no-ops preserving pending requests. Cease Fire/Attack on Sight share physical
weapon flags, preserve request lifecycle, and reject stale/invalid authority.

Schema23 preserves captured frame count, real40 shooting rankings, shared fleet
aliases, LastKilled/DamageDone, authored geometry and all5 mounted fire patterns.
World-owned target/ranking scratch is reused; no extra per-shot history is added.
Actual source Ship.Sight (not Squad.MaxSight) controls Scouting. Beacon spawn
registers newborn geometry, targeting/fleet metadata and minion lifetime before
vision. See README.md for the existing detailed contracts and remaining adapters.

One episode-owned NativeCommandCreditLedger consumes ordered immutable physical
hits, unattributed victim losses, capability finalization TSV, and first-sighting
strategic credit. Superseded commands remain owned until trustworthy episode final,
so a delayed projectile credits its launch outcome even after command completion.
Original selected strategy/reservation IDs and shooting eligibility survive
BombingRun/Charge redirects. Failed episodes discard known reservations.

First faction-wide ship sighting credits the current command with source float
multiply/clamp/truncation (3..40 TSV), independently of PPO shaping and shooting
eligibility. No initial command means no later retroactive credit. Beacons route
to canonical MotherSquad after Scout death if that squad still physically lives;
same-tick total death is rejected before roster reconciliation. Fire Barge death
captures the killer's active command for later friendly-blast secondary credit;
an absent death-time command uses the killer's later active command. Original
friendly-fire penalties and defender losses remain separate physical credit.

## Durable episode outcomes and recovery

EpisodeFinal/Aborted prepares one CommandOutcomesReady batch. The diagnostic sink
synchronizes it before the host checkpoints its original StoreCommands payload
and hash in DurableHiveMindConnection. Queued is neither sent nor acknowledged.
The previous gap was journal fsync -> in-memory prepared queue -> connection
checkpoint: process exit could strand a saved batch or cause duplicate reimport.

New schema2 journals have a cryptographically random incarnation and global append
sequence. PreparedOutcomeBatch carries that identity, position and SHA-256 of the
complete journal prefix. The SAME connection checkpoint atomically retains the
retry payload/hash and journal frontier/checksum, including after synthetic/live
200 acknowledgement or terminal409 reconciliation. One cursor is retained per run
journal, not per command; no competing outcome ledger exists.

recover_command_outcomes streams stopped journals from the beginning, checks
incarnation/append/world ordering, rebuilds validated duplicate/discard guards,
and imports only missing batches. Already accepted contents must match their
checkpoint checksum. Torn final lines are ignored; complete malformed records,
altered accepted prefixes and truncation behind the accepted position fail closed.
Replay keeps one cursor per world; writer and reader cap individual rows at64MiB.
Old connection checkpoints load with an empty cursor map. Schema1 journals lack
recovery identities and are rejected; never synthesize IDs or requeue old rows.

Operational paths:
- Optional run-config recover_notifications lists stopped schema2 journals to
  import into its existing connection_checkpoint before a new model/world starts.
  New notifications still needs a fresh path; prior worlds are not reconstructed.
- `cargo run --manifest-path native/Cargo.toml --offline -p bees-host --example
  recover_outcomes -- checkpoint.json journal.jsonl [journal.jsonl ...]` performs
  the same recovery without a model, window or network transmission.
Keep exclusive ownership of the original checkpoint and journal files. Missing
checkpoints fail rather than silently allocating another connection identity.

## Executed qualification

External disposable fixtures/receipts under B:\Bees\native-capability-qualification:
- journal-recovery-crash.txt: actual native Level completed with sighting credit;
  child exited73 after CommandOutcomesReady synchronized, before host checkpoint.
  Its checkpoint had no pending store request.
- journal-recovery-readback.txt: exact missing batch reimports once with original
  TSV3 and unused shooting discards. Restart/replay retain the original hash and
  payload. Synthetic503 preserves retries; synthetic200 retires them permanently.
  Synthetic409 preserves exact stale IDs without requeue, including after completed
  reconciliation. Torn rows, changed prefixes, duplicate outcome IDs, truncated
  accepted files, absent checkpoints and old checkpoint upgrade were exercised.
- journal-recovery-cli-1.txt / -2.txt: actual standalone executable queues1, then
  queues0/skips1 while retaining the identical pending request hash.
- vision_credit-journal-recovery.txt: eight real host sighting scenarios retain
  clamp/truncation, no-command, duplicate-observer and local-command behavior;
  actual episode finals checkpoint identical reopened StoreCommands payloads.
- chain_credit-journal-recovery.txt: real lethal shot101, death-time command201,
  later301 replacement and commandless-death fallback retain secondary credit
  through final episode and identical reopened retry payloads.
- workspace-journal-recovery.txt: workspace all-target/all-feature checks and
  all106 existing simulation tests pass. headless-journal-recovery.txt passes.
No new committed tests; existing warnings remain. Earlier MotherSquad and all5
ranged-command qualifications are in vision_mother-integrated-ae92.txt and
pulled_durable-integrated-ae92.txt; these predate only the journal recovery edits.

These are supplied executable fixtures, not authentic Unity exports, learned-policy
or live server receipts. Windows process exit/local disk recovery is qualified;
power loss, Linux execution, physical input/window timing and live distributed
learning/performance gains remain unverified. The configured runner recovery
startup is source-connected/compiled; its full model-worker startup was not run.
No interrupted world/unfinished command is reconstructed. Omitted recovery journals
are not automatically discovered; unattended full-game recovery is incomplete.

## Highest-value next work and complete objective

Connect actual socket submission/200/409 handling plus explicit targeting and
stale-reply settlement to this one retry owner. Preserve source eligibility and
original IDs; never settle delayed projectiles at command completion. Then close
asteroid/MapObject targeting fallback and authored dynamic factories.

Full replacement still needs the ordinary game launcher/modes, presentation/
sprites/animation/audio/input, scripted queues and Carrier/Drone lifecycle,
authoritative persistence, connected neural optimizer/trajectory/checkpoint,
distributed learning, evaluation and experiment scheduling, Windows/Linux execution
and measured speed improvement. Preserve one authoritative gameplay implementation
across the playable client, headless worlds, policies and evaluation.
