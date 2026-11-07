# Bees unified native rewrite

This directory is the beginning of the Unity-independent replacement platform,
created from the current `feat/experiment-queue-ui` branch.

## Native tactical view (development)

The existing policy runner can now open an optional native Windows/Linux-X11
window around the **same** `NativeHostRuntime`, model inference and fixed-step
Levels used headlessly:

```sh
cargo run --release --manifest-path native/Cargo.toml -p bees-host \
  --features client --example policy_rollout -- /absolute/path/to/run.json --view
```

Space pauses all worlds; N advances one fixed round while paused; Tab changes
the inspected world; arrow keys pan; the mouse wheel zooms around the pointer;
F fits the imported bounds; left-click inspects a nearby ship and selects its
source-authorized player squad. Ctrl-click adds squads; left-drag selects squads
whose real hulls overlap the box. Right-click moves the selected squads, once per
live squad lifetime; locked or unavailable squads are skipped. Move requests carry
world/episode identity, finalize prior commands through the normal outcome outbox,
and invalidate pending AI request authority before shared formation/path movement.
Escape or closing the window cancels unfinished episodes through the existing
durable abort/delivery boundary. The configured step duration
is preserved; a slow model slows playback rather than skipping battle ticks.
Status intervals still include all elapsed host time, including pauses.

The view draws authored primary collider outlines, current health, live
projectiles, obstacles and fading debris directly from borrowed combat bodies.
It is a tactical development view, **not** the finished sprite/UI/audio game.
No window-system dependency is enabled in the default headless build. Linux
interactive builds require X11 development libraries and a display; the native
simulation and offscreen renderer do not.

Either mode accepts `--capture final.ppm` to save the final inspected world's
pixels (world zero when headless). This path is relative to the launch directory,
must not already exist, and is reported with world/episode/tick identity.
Capture failure is reported after live episodes are stopped; it cannot bypass
their cleanup. Captures fit the original imported bounds independently of the
interactive camera.

Windows GNU compilation of all targets with/without the window feature and the
existing 106 simulation tests have been exercised. A local eight-ship,
collision-disabled presentation fixture opened a window, rendered 65 frames,
advanced 56 shared simulation ticks, checked picking and captured its frame
without rendering altering the world tick/RNG. This is presentation evidence,
not imported-policy-run, physics-parity, Linux, training or game qualification.
See `HANDOFF.md` for the remaining integration boundary and local evidence.

## Headless policy-only execution (development)

The executable `bees-host/examples/policy_rollout.rs` connects the exported
physical scenes to the **same** shared `CombatWorld`, multi-Level driver,
resident v26 TorchScript/ONNX inference, durable HiveMind identity and
notification delivery, and interval throughput counters:

```sh
cargo run --release --manifest-path native/Cargo.toml -p bees-host \
  --example policy_rollout -- /absolute/path/to/run.json
```

The single JSON configuration file requires:

- `run_id` (unique run incarnation); `stage_name`; paths
  `collision_matrix` and `stage_palette` to **actual** Unity exports.
- `fragment_spawn`: source `map_bounds: {min: [x,y], max: [x,y]}`,
  `half_map_extents: [x,y]`, `asteroid_max_speed_exclusive`, and
  `training_health_ratio` (number or null). These values come from the
  real Stage, not a guessed training scenario.
- `python` (Python executable path), `policy_worker` (normally
  `native/tools/policy_worker.py`), `model_manifest` (generated using
  the `policy_manifest` example), `connection_checkpoint`, and
  `notifications` (a new nonexisting JSONL path, separate per run).
  Relative paths are resolved against the configuration file's directory.
- `next_squad_item_id`: the source Stage's next unique
  `Pool.ItemCount` identity, strictly above all imported squads.
  The same monotonic allocator services every Level in this run.
- `broadphase_cell_size`, `decision_period`,
  `max_rounds`, optional `episodes_per_world` (default 1),
  `rounds_per_window`, `delivery_every`, and optional
  `timing_sample_period` (all step counts are positive).
- Optional `model_updates`: an ordered array of
  `{"at_round": 1000, "manifest": "next-model.json"}` entries.
  Rounds must be strictly increasing and less than `max_rounds`.
  Each round is the **earliest eligibility threshold** for a switch, not
  permission to change policies mid-episode. The runner splits its
  rollout at that threshold, pauses arenas as they finish, waits for
  all live episodes to complete and deliver their notifications, then
  changes models **before** recycling any world. It records both the
  requested and effective round, previous/new generation, exact model
  manifest SHA-256, and the cumulative evaluation at the boundary.
  A switch without remaining episodes, one that overruns the next
  threshold, or a run ending before a requested switch fails explicitly
  rather than reporting invalid mixed-policy benchmark outcomes.
  Each successful switch emits a `policy_segment` with the previous
  checkpoint's standalone evaluation; `run_final.active_policy_segment`
  covers the final model generation. These are differences of counters
  acknowledged by the durable notification sink, not estimates based
  on unfinished or partially stepped episodes.
  Each target manifest must have verified artifacts and the same
  model slot IDs. Failed changes abort the unfinished run.
- `levels`: a nonempty array of `{scene, world_id, level_id, seed,
  model_slots, groups?}`. Each scene is an exported **initial** Level;
  both IDs must be unique and positive, and `model_slots` maps exact
  Unity policy-team IDs (`"0"`/`"1"`) to verified manifest slots.
  For schema-16 exports, the actual participating Unity Agent instance
  IDs, source ship bindings and episode number are recorded in the scene
  itself; the runner derives `groups` automatically. If manually supplied,
  `groups` must match that source record exactly. Legacy schema-15 scenes
  can still supply `{"episode_number": 1, "participants":
  [{"side": 1, "agent_id": 123}]}` from actual external group ownership,
  never inferred hull IDs. Scripted opponents may have no group
  participants on their side. Legacy schema-15 group scenes are
  single-episode only: they lack the per-team movement settings required
  to replay alternating policy ownership safely. A source controller
  whose initial team disagrees with the configured episode rejects
  the run. On subsequent schema-16 episodes, the native Level advances
  the episode number, alternates team routes and source movement limits,
  and selects fresh source-equivalent distinct policy coordinate frames
  from a world-specific reproducible seed.
  Omit `groups` only for non-group gameplay.


The runner checks source identities, model routing, collision settings and
Stage consistency **before starting**. It emits run-tagged JSON interval
measurements on stdout, including completed episodes, wins by source side,
draws, timeouts, mean episode duration, and real group rewards. Evaluation
counts advance only after their notifications have been synchronized and
acknowledged, not when an unfinished physical world merely becomes idle. Group-reward and terminal diagnostics include typed
numeric fields; ELO markers remain separate from PPO reward. This diagnostic
log is **not** the complete observation/action/value/log-probability data
needed to train a policy. Its durable sink synchronizes bounded ordered event
chunks (up to 128 records or 256 KiB) and acknowledges only the actually
committed source prefix; a previously created output path is never overwritten.
Completed worlds can be recycled up to `episodes_per_world` times through
`NativeHostRuntime::reset_world`, with fresh monotonic squad lifetimes and
per-episode deterministic seeds. Each replay uses the exact initial exported
scene bytes cached once per canonical source file at run startup, not
a newly randomized Unity scene or a training-only variant. Multiple arenas
may share the same immutable template, but each arena has independent mutable
state and duplicate source Squad ItemIds are reassigned from the verified
Stage-wide allocator. The global `max_rounds` budget bounds the run, and
unfinished episodes are explicitly canceled with `episode_aborted` rather
than awarded invented terminal outcomes. The event
`payload_debug` is **diagnostic text**, not an optimizer or training-data
wire format. This harness deliberately does not connect to live HiveMind
WebSocket gameplay or provide PPO/MA-POCA updates, checkpoints, graphical
rendering, full parity or production durability of all app-side effects.
Do not use its partial trajectories as valid learner data. Native
Windows/Linux execution and throughput are not yet verified.

### Resident policy updates

`ResidentPolicyProcess::reload_manifest(path)` updates the real policy
weights **between complete inference calls**, without restarting a native
Level or respawning the Python worker. Version 2 of the binary worker
protocol accepts one explicit `BMOD` manifest-load request, returns
`MACK` only after SHA-256 verification and successful loading of ALL
model slots, and rejects incompatible signatures or slot sets with
`MNOK`. The startup handshake returns the exact source manifest digest;
reload requests carry the expected manifest SHA-256, which is checked
**before** loading weights, and successful receipts echo that digest.
The runner emits both the manifest digest and process-local policy
generation for every model transition. A rejected update keeps the existing policy live; a malformed
or incomplete protocol response poisons the worker and requires explicit
restart. Reloads are **not** triggered by filesystem polling: the caller
explicitly requests them or configures the headless runner's
`model_updates` schedule. The caller must retain model artifact hashes,
run/episode identities and policy generation for scientific attribution. The initial model
generation is zero and each accepted replacement increments it. The
status interval reports the last validated generation and exact
authenticated MA-POCA actor rows. This is not yet a native PPO learner;
the inference-only TorchScript/ONNX model manifest does not provide
action log probabilities or a group critic. Loading a replacement model
can temporarily require space for both the old and new weights.

## Unity scene import (schema 19)

**Bees > Native > Export Selected Level Physical Scene** captures a
source-authored initial Unity Level, not a resumable match checkpoint.
The scene captures the active self-play episode, both team-specific
movement settings and registered Unity Agent instance group membership,
including its exact currently bound ship (if any). Earlier schemas require
a fresh export to supply the additional authored collision and movement data.
It records original ship/weapon, squad, obstacle, mining and destructible
MapObject identities and live collider geometry, plus real policy
controller/frame/capability inputs, episode starting TSV, discovery
baselines, navigation frame and Stage damage settings. Unsupported
geometry, missing source identity and partial active combat state fail
closed rather than generating plausible replacement values.

The scene additionally records actual `Ship.SizeClass`, configured ship and
Beehive pixel sizes, faction IDs, and active Beehive HealCollider / WarpGate
WarpCollider geometry in the owning ship's body frame. Boxes, single-path
polygons and isotropically transformed circles use one exporter shared with
MapObjects. Older scene exports must be re-exported; missing inputs are not
estimated from health, hull radius or old capability-observation bits.

Schema 18 also carries the actual turning radius, sprite half-extents and
`Level.ActivateCollisionAsteroids`. The builder installs shared game movement
profiles for all ships (including stationary squad members). Before each fixed
step, queued navigation uses live world colliders and the Level's reusable search
workspace; quiet steps do no raster/search work. Completions retain ship/request
lifecycle validation. Current-episode job totals appear in host status gauges.
Active non-policy routes are rejected: a target position alone cannot restore
their waypoint, retry and command history. Export a fresh scene.

Schema 19 adds exact squad user-control/input/lock permissions, stored formation
offsets, path clearances and Level.HasObstacles. Survivor offsets update after
casualties through the shared Level rules. The fresh-scene exporter rejects
existing commands and scripted queues; this format does not restore that history.

The shared scene builder installs every ship's original-health collision
profile and support zones, then binds real policy mining/healing/warp
eligibility. Readiness comes from the world-owned contact/cooldown state.
The source eligibility bits are checked against the exported faction, type
and size inputs. New ship hull geometry is ready at tick zero, so initial
observations and masks do not require an artificial warm-up physics step.
Signed FleetShip identities are preserved: negative training/campaign/minion
IDs remain transient; positive IDs alone do not replace the separate
`persistent_fleet` flag used for reward/economic accounting.

Schema 17 connects all five supported ship specials to the same scene builder:
actual bomb/charge power, optional Striker carrier relation and Scout Beacon
prefab/Create/Setup inputs. Forward carrier references are configured only after
all ships exist. Standalone Strikers are represented without an invented Carrier;
their observation parent follows the world's live carrier-death reassignment.
The exporter rejects consumed bombs, charges or Scout cooldown/resource history:
this remains an initial scene, not a resumable checkpoint.

Beacon templates retain real source durability, TSV, motion, size class, sight,
registration and optional side-population cap. Both game and policy Scouts spawn
through CombatWorld. The Level registers the new Beacon's transient fleet value,
perception and vision before vision/reward/observation consumers run; the ordinary
event processor registers its actual minion Squad. Beacon hulls and original-health
collision profiles exist immediately, and their zero setup rotation matches
`Pool.CreatePooledBeacon` / `Ship.Setup`. No model route is fabricated for minions.
Hosts providing custom Level metadata must implement `register_scout_beacons`
when enabling Scouts; missing spawn data fails the Level step explicitly.

Read through `NativePhysicalScene::read`; launch actual policy-only
Levels with `NativeSceneAssets::build_imported_policy_runtime`.
The host supplies loaded learner/opponent model slots, an authentic
minion-lifetime allocator, and a durable event sink. It runs all imported
Levels through the same `CombatWorld`, game rules and batched inference
interface. Each Level keeps its own actual navigation frame, observation
memory and asteroid spawn map geometry; only immutable Stage assets and
model weights are shared. Mixing physics fixed-step durations or using snapshots from a
different Stage than the loaded asteroid palette is rejected.

This still does not restore prior projectiles, learned discovery or
command/reward history. Full native game presentation, optimizer and
distributed learner remain incomplete. Native Windows/Linux runtime
qualification has not been performed.

## Non-negotiable boundaries

- Exactly one authoritative set of gameplay rules must power both the rendered
  game and every headless/training/evaluation world. Do not create a simplified
  training ruleset.
- Gameplay state, clock, RNG, and entity ownership are per-world, never process
  globals. Keep presentation, server I/O and ML libraries outside `bees-sim`.
- Optimize the measured bottleneck. Do not replace mature native inference or
  physics libraries merely to reduce the dependency count.
- A failed update must not silently continue from a partially mutated world.
- Preserve existing user data, Hive Mind outcomes, policy ABI, scenarios,
  curriculum, metrics, deployment, authentication and experiment semantics
  until a deliberate compatibility migration is established.
- Linux, Windows and macOS are required targets for both the game and headless
  workers. Native development must work without the Unity editor.

## Native Level and inference integration (development status)

`bees-gameplay::level_rules::NativeLevelRules` is the current canonical
gameplay-to-simulator adapter. A `NativeLevelState` owns the real
`CombatWorld`, one `NativeLevelEventProcessor`, and Level-supplied
persistent FleetShip metadata and minion SquadLifetime allocation.
`NativeLevelRewardMetadata` supplies actual fleet values; the separate
`NativeLevelCommandContext` supplies fresh, Level-order squad snapshots
and authored navigation when live Hive Mind commands need scaled timer
callbacks. Never construct these inputs from guessed hull sizes.

For each native Level episode, register real squad membership before
stepping. Each successful fixed step runs one policy/combat transaction,
reconciles ordered ship deaths and Scout minions, computes hit/mining/shot
rewards and termination, then advances any surviving timed commands.
The world remains failed on partial event/metadata errors; no sample
may be exported from a failed step. A native Beehive healing capability
now accepts the actual Beehive child HealCollider and authored ship-size
eligibility; a touching eligible Bee can restore up to 50 health per
successful action with a one-second cooldown. That action emits its
real TSV improvement through the same episode shaping budget. Policy and
game-command healing now share the physical health/TSV mutation and
capability event. Four-slot Beehive reservations are globally owned by
CombatWorld, released on ship/Beehive retirement and queried through the
authored healing-zone index. The game-command entry
`heal_reserved_command_ship` enforces reservation ownership. HiveMind
Heal now selects live same-side Beehives in source distance order, sorts
damaged ships by the original (not training-adjusted maximum) health,
reserves shared slots, moves each waiting ship to its assigned hive,
starts 1-second healing on exact authored HealCollider contact, and
reassigns waiting ships when slots open. Command completion, timeout,
replacement and squad retirement release their reservations. The Level
adapter **must** call `configure_game_ship_original_health` with each
ship's true `Ship.OriginalHealth` before Heal is requested; missing
metadata fails command activation rather than guessing from MaxHealth.
Render/Level integration and native runtime qualification remain pending. WarpGate
returns now require the real WarpGate child collider supplied through
`configure_warp_gate_zone` and a living non-WarpGate Human policy
controller registered with `configure_policy_warp` using the authored
HumanSide ID. A successful overlapping return emits `ShipWarped` before
`ShipDied`, credits preserved live ShipVitals TSV to the same bounded
shaping stream, and lets the separate economic ledger retain mined cargo.
Policy masks reject warp actions without an authored active gate contact;
the rendered Level still owns any WarpGate sound/presentation.
Native Factory and CarpenterBee policy mining uses the actual
MiningAsteroid collider, its authored health, a five-unit extraction rate
and a five-second cooldown. Depleted asteroids retire from the shared
physics/discovery registry, with an event for the Level's presentation and
mining-command lifecycle. Before tick rewards are processed, the Level
must implement `NativeLevelRewardMetadata::credit_mined_minerals` to
update its persistent FleetShip value. Missing or inconsistent metadata
fails the Level step rather than crediting fictitious minerals. Mining
contributes to economic accounting, not an additional immediate tactical
capability reward. The same world-owned extraction routine can
distribute a five-mineral-per-ship command yield with the exact integer
remainder order, and indexed nearest-target/contact reads expose the live
asteroids. The native HiveMind Mining command now uses that same extraction
routine. It selects the nearest live asteroid, samples a seeded approach
point from the authored collider bounds, registers miners on real contact,
uses three-second movement and five-second mining timers, stops moving
five seconds after all miners arrive, and uses the source 600-second
HiveMind timeout. Actual mined minerals are preserved in the finished
command's TSV and in fleet economic accounting. Mining events created
after the combat reward pass are persisted and queued through the same
Level outbox; no separate mining simulation or tactical double-credit.
Cumulative mining workload counters require no per-step logging.
The rendered fleet/command adapter and physical Windows/Linux
qualification still require integration.

HiveMind Scouting now uses source five-second movement polls, the
authoritative squad sprite-extents center, ten-second Scout beacon polls,
a 300-second timeout, and five-second completion after first verified
side-wide enemy-ship discovery. The owning Level must supply
`NativeScoutingContext` with actual `Squad.MaxSight` and
`ConfigData.Configuration.HumanSide`, configure each game Scout's
authored Beacon via `configure_game_scout`, and call
`record_verified_scouting_sighting` with the true reward squad lifecycle
(including a Beacon's MotherSquad). Game Scouts retain Beacon charges and
cooldowns when commands change; both game and RL Scouts use the same
world-owned physical Beacon spawn. `ScoutBeaconDropped` is durably
published after immediate native minion-squad registration using the
same `NativeSquadRoster.register_scout_beacon` path as policy spawns.
The graphical Level must still construct the rendered minion squad from
that change before acknowledging it; runtime qualification remains pending.
Command TSV vision credit must come from the
actual Level observer/outcome, not from guessed sight-range polls.

`NativeGroupRewardDelivery` is an optional per-episode MA-POCA delivery
adapter. The runner registers participating agents, consumes side-wide
immediate and per-step group events once, sends the separate ELO marker
as transport-only metadata (not PPO reward), then ends both participating
side groups on terminal completion. `pending_notifications()` retains
sparse, tick-stamped rewards, group events, completed commands, squad
changes, and `Gameplay(CombatEvent)` capability outcomes across many
fixed steps. Only mining, depletion, Beehive healing, Scout Beacon spawns and
WarpGate returns are forwarded through this gameplay channel;
high-volume shot events
are not duplicated. The Level adapter must apply depletion cleanup,
healing presentation and warp sounds before acknowledging those events;
it must not credit capability rewards twice. The host durably consumes
and `acknowledge_notifications` only for the delivered prefix. Drain promptly
after `record_verified_discovery` to preserve Unity's immediate timing.
Do **not** separately deliver an event returned by another path.

The *connection*, not each Level, owns `HiveMindSession`. After parallel
rollout windows, inspect the persistent `pending_session_retirements`
outbox on each world and call `flush_retirements_to_connection` through
`Simulation::world_mut`. This prevents squad deaths on earlier ticks
from losing their pending-request invalidations. Pending OutcomeIds
still require ordinary persistent StoreCommands settlement. Before
episode reset, use `reset_level_after_delivery` to reject a discarded
notification, unflushed retirement or unresolved active command. Normal
terminal episodes finalize surviving HiveMind commands and queue the
remaining squad lifetimes for shared-session retirement before emitting
the final episode notification; this never fabricates physical ship deaths.
For a failed world, call `abort_failed_level` before reset: it publishes
`AbortedCommand` notifications carrying the exact unresolved primary and
shooting reservations for explicit discard/reconciliation, not TSV credit,
and queues all surviving session lifetimes. Persist/acknowledge those
notifications and flush the connection retirements before resetting.
The same `cancel_level` boundary deliberately stops a live episode;
both paths emit one `EpisodeAborted` marker even with no active commands.
The learner must reject the partial PPO trajectory on that marker, not
treat the canceled episode as a normal terminal outcome. A live world
with unretired squad lifetimes cannot be reset just because it has no
active command: cancel it first and flush the shared session.
Normal terminal episodes are reported as completed, not failed steps.

`NativeLevelInferenceBatch` shares the full v26
`PolicyPerceptionSnapshot` encoder and live CombatWorld action masks;
every model output must match world ID, episode revision and tick.
`NativeWorldPerceptionAssembler` reuses the world's true self/parent,
allied and VisionCache projections, with Level-only weapon, map and
economic values required from the caller. `step_batched_levels`
combines multiple world observations into one validated model call.
`NativePolicyInference` is the backend contract, not an implemented
neural network. `NativeLevelBatchDriver` now owns per-world v26 observation
assemblers, genuine batched model inference/step dispatch and guarded episode
reset, and routes BeesServer strategy replies through the shared
`HiveMindSession` by exact source SquadLifetime. Retired replies return
explicit StoreCommands discards; accepted targeting selections are routed
to their authentic Level but still require Level-side installation and
credit attribution. `deliver_all_level_notifications` acknowledges only
the durable, source-ordered prefix accepted by the user's output sink.
The rendered game and headless worker must provide the SAME
`NativeLevelCommandContext`, `NativeLevelObservationContext` and
`NativeLevelActivationContext` game-derived values; no alternative
training-only simulation is created. After any MemberLost event, the Level
updates source Ship.OffsetFromCenter and composition bans using the
authoritative sprite catalog, and in-flight command member snapshots
rebase without restarting timers or server reservations.

A native client or worker initializes a `Simulation<NativeLevelState,
NativeLevelRules>`, registers its worlds with
`NativeLevelBatchDriver::from_simulation`, then calls
`run_window` with a real `NativePolicyInference` backend, the one
connection-owned `NativeHiveMindConnection` (which owns the shared
`HiveMindSession` and StoreCommands retry ledger), and its durable
`NativeLevelNotificationSink`. The explicit delivery interval can be
1 for rendered gameplay or larger for bounded headless windows. Incoming
strategy/targeting replies use `receive_strategy_response` and
`receive_targeting_response` to route by the original Squad lifetime;
the latter returns the still-uninstalled targeting reservation to the
source Level. On a failed step or intentional stop, call
`stop_world_and_deliver` with the proper failed/normal stop kind,
then `reset_world_after_delivery` only after every notification and
connection retirement has been handled. No method retries an ambiguous
partial world update, fabricates a model action, or commits unearned
training outcomes.

`NativeLevelInferenceBatch::poca_dimension_activity`
exports the source PPO's 22 activity weights from the *same* validated
observations, masks and selected actions, requiring live POCA groupmate
counts. `poca_normalize_advantages` separately normalizes genuine
trainer-computed advantages using inverse live group-size weights; it does
not construct them from rewards or actions. Its returned moments and
effective sample weight support learner diagnostics. Native code does not
generate old log-probabilities, policy versions or centralized group-critic
estimates; those must come from the real trainer/model. The host still must supply an actual CPU/GPU model. **This is not yet a runnable game or distributed
RL training replacement.** Renderer/input, full remaining gameplay,
model execution, policy optimization/checkpoints, transport, and
cross-engine/Windows/Linux qualification remain outstanding. No
performance or parity gains are claimed without executable measurements.

The OS host now includes a **blocking, certificate-verified WSS client**
(`bees-host::socket::NativeHiveMindSocket`) for the production
`game` WebSocket subprotocol. It requires an authentic Steam
`AuthTicket` in `setup-level`/`reconnect-level` and uses the single
connection's durably committed request-hash allocator. One socket can
register multiple Levels against the same server Game. Only a explicitly
opted-in **loopback** `ws://` endpoint is permitted without TLS for
development; no insecure TLS certificate bypass is supported. The socket
validates setup acknowledgements, authenticates before issuing strategy
or StoreCommands requests, checks current request ownership, and refuses
to send a StoreCommands payload different from the saved retry ledger.
Its read API returns every unrelated server frame for the
`NativeHostRuntime::receive_frame` and remaining feature owners.
After reconnect and setup ACK, call
`NativeHiveMindSocket::replay_pending_stores` to send exactly the durable
pending original StoreCommands requests (not 409 reconciliations). The
socket exposes small cumulative counts of frames/bytes, setup successes
and transport/protocol errors, suitable for sampling between rollout
windows without expensive per-frame traces. Incoming frames are bounded
to the deployed server's 8 MiB limit and redirects are not followed,
preventing WSS downgrade.

The rendered host must run its blocking socket I/O on a separate
network thread; only the gameplay/Level driver runs in the game loop.
On disconnection, call `DurableHiveMindConnection::retire_socket`,
reconnect/authenticate, then inspect pending StoreCommands for safe
original-hash retries. Connection transport does not replace server
outcome persistence or claim a completed gameplay runner.

## Hive Mind / server migration boundary

The native workspace now also contains `bees-hivemind`, a separate,
network-independent protocol and outcome-ownership crate. It preserves the
deployed `get-matchup-strategy`, `get-strategy` and
`store-commands` JSON fields, server command IDs **1–15**, shooting
IDs **1–40** and distinct targeting IDs **1–40**, exact 53-bit request
hashes, and unsigned matchup identities. Request hashes are allocated once by the client
transport for **all** message types. The transport-neutral
`NativeHiveMindConnection` now owns Unity-compatible 53-bit hash
allocation, the single shared `HiveMindSession`, and the existing
`PendingStoreCommands` retry/409-reconciliation ledger. Its
`NativeConnectionCheckpoint` contains only durable retry ownership and
the monotonic allocator (not socket credentials or transient Level
lifetimes). Persist a checkpoint before transmitting a newly created
request, and after acknowledging/reconciling a stored outcome; otherwise
a process crash can cause replay or Hash reuse. The actual WebSocket and authentication remain client work.

The connection checkpoint now initializes its 21-bit client request-hash
prefix from operating-system cryptographic entropy on first creation.
Subsequent launches restore precisely the persisted prefix and counter;
no application or shell script must invent a shared static prefix.

For production-style file-backed clients, use
`bees-host::durable_connection::DurableHiveMindConnection`: it wraps the
shared session and checkpoint with a fail-closed send boundary. Request
hashes, strategy/targeting requests and StoreCommands batches are returned
to the socket owner only after checkpoint persistence succeeds. Server
StoreCommands acknowledgements and 409 reconciliation changes are also
checkpointed. After a write failure the instance stops issuing requests
until restored, rather than returning an unsafe sendable payload. Pending
StoreCommands hashes and terminal 409 reconciliation hashes are exposed
without duplicating the retry ledger, enabling deterministic reconnect
scans. The adapter assumes exclusive ownership of the checkpoint path.

The native host adapter `bees-host::checkpoint_file` provides optional,
cross-platform, file-backed persistence for this exact connection checkpoint:
it streams JSON to a unique same-directory temporary file, flushes and syncs
the file, replaces the checkpoint atomically, and syncs the directory on Unix.
Call `ConnectionCheckpointFile::persist` before transmitting any newly
allocated request and after a StoreCommands acknowledgement or reconciliation.
`load` fails closed on corrupt or incompatible state rather than silently
resetting request IDs. The host must ensure a **single writer** per checkpoint
path; the adapter does not implement network delivery, authentication, locking,
or a durable Level-notification sink. On Windows, replacement is atomic when
the filesystem permits it, but the standard library does not expose a portable
parent-directory sync; sudden-power-loss persistence must be qualified on the
actual filesystem.


Multiple Levels may share one WebSocket
and one server Hive Mind Game. Each strategy request captures both the requesting Squad lifetime and
its original targeted enemy lifetime, so a late response cannot adopt
a replacement pooled squad with the same ItemId. Target-dependent
commands reject that stale lifetime and preserve their returned
OutcomeIds for explicit discard; no new server wire field is needed.

For completed commands, the game must settle every issued primary/shooting
OutcomeId, independently decide whether a targeting reservation earned TSV,
and persist a StoreCommands submission before sending it. Native
`PendingStoreCommands` holds submissions until the authoritative server
responds. Status 200 acknowledges durable commit. A status-409 response
with `StaleOutcomeIds` is **partially committed** in the deployed server:
valid siblings have been written, so the native ledger retires that batch
but persists the unresolved stale IDs in a separate reconciliation record
rather than retrying already-committed outcomes. Only explicit reconciliation
retires that record. Other failures retain their pending submission. Transport authentication, reconnection, durable
client retry storage, the native squad-command executor, and the server-side
learning/persistence implementation remain separate work. **The existing
BeesServer production behavior has not been replaced in this increment.**

## Game command integration

`bees-gameplay` connects validated `bees-hivemind` selections to the
`bees-sim` combat world's single native movement implementation. It
preserves the server's learned strategy ID separately from the executable
Unity command (e.g., Aggressive/Circle/Swipe/In-and-Out become BombingRun
for bomber-only squads or Charge for barge-only squads). Ordinary squad
Move and Stop use authored ship offsets, 20-step formation compression,
nearest walkable-center fallback and each ship's shared A* path queue.

The source-matched movement phases of HiveMind **Hold**, **Random**,
**Patrol**, **Retreat**, **Closest Friendly**, and **Guard** are now wired.
**CircleSquad** now shares the same director and canonical movement engine:
the Unity initial three-second immediate poll, tracked approach, quarter-second
circling after contact and authored angle/destination-spacing rules are ported.
Its Level adapter must implement `NativeLevelCommandContext::circle_command_snapshot`
with actual squad bounds centers, MaxRange and the exact
`Ship.IsShipWithinRange` / per-weapon target-validity result. The default
deliberately fails the episode rather than treating center distance as weapon
eligibility. Unity's `Weapon.IsShipValidTarget` specifically checks the
RangeCollider-maintained `ShipsWithinRange` contacts and line of fire
(Physics2D.Linecast to the enemy collider's closest point), plus any subclass
override. A center-distance test would change command behavior.
`assemble_circle_snapshot_from_live_weapons` implements the source's
per-ship/target-squad predicate over the authoritative live roster, while
the host supplies actual authored weapon validity and squad geometry.
The live adapter and executable parity qualification remain pending.

**RightSwipe** and **LeftSwipe** also use the canonical command timer, shared
tracked movement and the source command's ranked target FIFO. The Level's
`swipe_command_snapshot` provides authored enemy-squad range, live squad
bounds, and GetShips-ordered targeting metadata only when a swipe timer fires.
`assemble_swipe_snapshot_from_live_weapons` reuses CircleSquad's exact
per-weapon range predicate. This port deliberately rejects missing adapter
data rather than fabricating a shooting target. The real Level adapter,
weapon/squad targeting integration and executable qualification remain pending.

**InAndOut** uses the same source-backed target FIFO and tracked approach as
Swipe, with no duplicate targeting algorithm. It chooses Unity's random
return point at activation, using the near-enemy conditional range. After
every living squad member has a valid weapon target, it issues one ordinary
formation Move back to that requested point and completes when all squad
members have reached their destination. It shares the existing
`swipe_command_snapshot` Level adapter because both commands require
identical live enemy membership, weapon validity and targeting metadata.
The missing authored Level adapter still fails closed rather than treating
distance as weapon eligibility.

Commands now carry `NativeCommandOrigin::HiveMind` or a Level-owned local
player/scripted identity through the same director, timers and completion/abort
outbox. Local activation through `activate_local_command` queues pending AI
authority retirement before inbound replies can apply. Server IDs remain solely
in HiveMind origins. `FinishedNativeCommand::settle` returns a typed
`NativeCommandSettlement`; local records retain their value and any independently
issued targeting reservation without inventing command/shooting reservations.
Local attack inputs support Aggressive, Charge and BombingRun, plus scripted Hold.
Other local commands require their exact chosen regions/targets and currently
return Unsupported. Player attack UI and complete scripted queues remain unfinished.

**Aggressive** uses the shared ranked target queue and authoritative
tracked ship orders, but unlike Swipe or InAndOut advances its attack
orders one ship per native simulation step, matching the command's
frame-spread coroutine *order*. Its 3s-to-0.25s scaled timer transition,
three consecutive per-ship weapon-range confirmations, position hold
and timeout behavior are implemented in the same command director.
The `aggressive_command_snapshot` adapter must supply actual weapon
validity, both some/all enemy-target range predicates, real live enemy
defenselessness, and current GetShips-ordered target metadata while an
approach coroutine is active. Its `need_all_target_range` argument
permits skipping the expensive all-target/defenseless checks until a
due timer enters the position-hold phase; the snapshot uses `None`
for unrequested values, never invented `false` results. The helper
`assemble_aggressive_snapshot_from_live_weapons` computes the stronger
all-target range predicate using the original Level roster and authoritative
per-weapon validity. The Level still needs that adapter. Unity's coroutine
resumes on *render frames* rather than simulation fixed steps; exact timing
parity across variable render rates is not established by this port.

Random finishes at arrival or command timeout; Patrol samples Unity's four-corner
route, rotates waypoints on its 3-second scaled callback, and uses the
separate configured patrol timeout. Retreat uses the Level's live target
squad position and weapon range to move directly away from a nearby enemy;
when already sufficiently far away it stops and finalizes after 3 seconds.
Unlike the other strategies, moving Retreat has no general timeout in Unity,
and its timer ends when all squad ships arrive. Delayed server responses must
still match the originally targeted enemy squad before activation.

Closest Friendly selects the nearest eligible living same-side squad in
Level registry order, excluding itself and squads already running Closest
Friendly. It retains the selected squad's full lifecycle identity and
rechecks the live target on each three-second scaled command callback
(the first callback is delayed, unlike Patrol). It issues
`MoveToTrackedPoint` formation orders instead of blindly invalidating
pathfinding: active search and failed-search retries keep ownership, and
endpoint drift less than Unity's eight-unit threshold does not restart
navigation. Following ends when the original squad disappears or the
configured follow time elapses. The three-second polling interval makes
Unity's additional one-second path-replan throttle redundant for this
specific command; the shared tracked-movement method is not yet suitable
for arbitrary faster-following callers. Missing eligible targets leave
both server OutcomeId reservations for explicit discard rather than
recording invented command rewards.
Guard picks the nearest eligible non-guarding same-side squad once and
retains its exact lifecycle identity. It assigns one of four guarding slots
(up, left, right, down) based on the number of current guards of that target,
then tracks the protected squad's center plus the guard's live bounds width
or height and four units. Its timer starts immediately and expires at the
configured guard duration; death of the protected squad ends guarding early.
Within 1.5 units of the guard destination, the native command sets speed to
the protected squad's slowest authored ship speed, clamped per ship to its own
base speed. Guard finalization, replacement and retirement restore the
surviving ships to their authored speed cap. The Level's current squad
registry supplies the active command, protected target, bounds, and speed;
this includes user/scripted Guard commands, not only native HiveMind orders.
When no eligible protected squad exists, the host must both discard the
server reservations and ban Guard for subsequent strategy requests. These
live relationships are not inferred from ship proximity or invented by the
training simulator. Unity's per-fixed-step movement speed reset and
Squad.IsMatchingSpeed behavior still need end-to-end parity work when the
full native Level update loop is implemented; the command's speed-setting
and restoration paths alone do not prove full Guard parity.

FullRetreat now selects the nearest live same-side WarpGate, registers
all non-gate squad members in the shared world-owned WarpGate session,
follows the gate every three seconds, queues physical WarpCollider
contacts and waits for the authored 2.375-second opening readiness.
An arrival at an already-ready gate immediately commits the waiting
group; the two-second poll handles earlier waiting ships when the gate
first becomes ready. The next canonical fixed step performs
ShipWarped/EndKill and normal roster, economic and terminal accounting. Actual Striker/Drone warp outcomes subtract their
preserved TSV from command credit before squad death can finalize the
command. Replacement, timeout and squad retirement release only the
command's participants, retaining same-gate registrations transferred
to a replacement. FullRetreat is source-integrated but still requires
runtime parity and transport qualification.

Commands update squads in stable lifecycle-ID order so independent worlds
do not depend on randomized hash-table traversal.
The game must supply real squad bounds, membership, configured speeds,
live enemy squad identity/position/range where required, shooting/targeting
state and scaled update time; the director does not invent missing game state.
The ship asset catalog now retains all 24 Unity ConfigData.ShipSizes values
in world units, separate from collision hulls; the Level's geometry
helpers use those source sizes for squad centers and widths. Command
activation must reuse each ship's actual saved OffsetFromCenter rather
than deriving new offsets from its current pose; source SetOffsets only
refreshes them at explicit formation/membership boundaries. Pathfinder
clearance is requested only for mobile ships on obstacle-bearing maps.
Squad movement and command polling use the world's canonical indexed ship
lookups instead of rescanning every ship for each member.
Unported command types return an explicit Unsupported result with their
server reservations intact. Command reward
totals and unused shooting/targeting settlement still belong to gameplay
attribution, then the separate HiveMind durable StoreCommands pipeline.
The shared native Level step/observation/command/notification driver is
wired, but no runnable full-game client, model backend, authenticated server
adapter or executable game/training binary exists yet.

The `bees-host::runtime::NativeHostRuntime` now unifies the
simulation, real model backend, multi-Level inference driver, durable
connection, source-ordered notification sink, stop/reset and sampled
throughput reporting under one cross-platform rendered/headless API.
This does **not** provide the missing source-authored Level adapter,
loaded neural model, WebSocket implementation or rendering frontend.

## Low-overhead performance and command measurements

The same host crate exposes `status::NativeStatusSampler` for
allocation-free, per-window telemetry: it samples the authoritative
`NativeRuntimeStatus`, uses monotonic wall time to compute actual completed
world-steps/s and inferred policy rows/s, and exports low-cardinality JSON
records with physics-work deltas, request backlogs, model-forward timing
denominators and an explicitly approximate, *sample-only* physics latency p95.
The sampler rejects counter resets rather than producing false throughput
spikes. Sampling runs between rollout windows, never per ship or per tick,
and does not claim to measure GPU execution time when the model backend
does not synchronize.



`Simulation.measurements()` provides cumulative cross-world
per-fixed-step counters (ticks, decisions, events, active-body snapshots),
plus integrated body counts, broadphase candidates, narrowphase tests and
contact entries already counted by the shared physics core. Each
`World.measurements()` also exposes the same bounded counters for its
current episode; `reset_episode` clears only that world's measurements,
not the simulation's cumulative totals. Wall-clock timings are collected
**only** when the caller enables a nonzero sample period; the fixed-size
log2-nanosecond histogram provides bounded-memory approximate latency
percentiles via `sampled_step_percentile_upper_nanos`. Failed updates
track sampled elapsed time separately so they cannot bias successful-step
throughput. When `Simulation::set_timing_sample_period` is enabled, selected
successful batched gameplay steps also measure host time separately for
observation assembly, inference/validation and world stepping.
`NativeBatchedStepCounts.phase_timing_sampled` distinguishes genuine
samples from zero-valued unmeasured calls. Rollout model-forward timing
uses the same sampling cadence, and
`NativeInferenceMeasurements.model_forward_timed_calls` and
`model_forward_timed_rows` provide denominators for sampled wall times.
Standalone `NativeLevelInferenceBatch::infer` continues to time every
model call. Accelerator work is included only when the backend
synchronizes. Snapshots can be
subtracted for experiment windows; the histogram can be aggregated without
storing every frame. Sampled timing does not yet include an attached native
HiveMind command update or an end-to-end PPO training pass.

`NativeSquadCommandDirector.measurements()` independently reports update
calls, active commands examined, recurring and timeout callbacks, started,
completed, superseded and retired command counts, and peak active commands.
The director uses a lifecycle-ordered map so command iteration is
deterministic and does not allocate or sort an owner list every frame.
The tradeoff is logarithmic command insertion and removal; the relative
performance needs profiling at 1v1 and large-battle sizes. These are
**instrumentation and algorithmic changes**, not measured speedups:
runtime comparisons remain necessary once a native executable loop exists. The shared simulation
stays platform-independent for Windows and Linux.

## Resident neural inference backend

`bees-host::policy_process::ResidentPolicyProcess` now implements the
actual `NativePolicyInference` model boundary, not a fake policy. One
resident Python model worker holds TorchScript or ONNX model weights on
CPU or a configured GPU provider (`cuda` is PyTorch's ROCm device
name on supported Linux builds);
it receives one dense, little-endian binary v26 batch, groups rows by
model slot, applies the real discrete masks through the model, and returns
exactly sixteen continuous and six discrete actions per row. Headers bind
replies to a monotonically increasing batch sequence. The worker
handshakes the **entire** frozen `POLICY_SIGNATURE` and version 26
before accepting a single observation. Rust sends zero-copy float slices
on little-endian CPUs; the model process never receives or mutates game
world state. A malformed/incomplete response poisons the stream and the
simulator rejects the entire batch before advancing physics.

Launch with
`ResidentPolicyProcess::python_worker("python", "native/tools/policy_worker.py", "native-models.json")`.
The manifest must contain `format: "bees-native-model-v1"`,
`abi_version: 26`, the *exact* `policy_signature` from
`bees_sim::policy::POLICY_SIGNATURE`, and a `models` array. A
TorchScript slot uses
`{"slot":0, "backend":"torchscript", "path":"policy.pt", "device":"cpu"}`.
The loaded model must implement
`forward(observation[N,7743], allowed_mask[N,15])` returning
`(float32[N,16], integer[N,6])`, with the original trained action
sampling/masking and authentic weights.

Existing exported ONNX policies can instead use an explicit slot mapping,
for example
`{"slot":1, "backend":"onnx", "path":"policy.onnx",
"providers":["CPUExecutionProvider"],
"inputs":{"observations":"obs_0", "allowed_mask":"action_masks"},
"outputs":{"continuous":"continuous_actions", "discrete":"discrete_actions"}}`.
Names must match the actual exported model; if the model instead emits
one already-decoded 22-column action tensor, use `"outputs":{"combined":"actual_name"}`.
An exported ONNX policy needing additional recurrent/memory inputs or
unsupported output layouts is rejected rather than supplied fabricated
values. The model's own inference graph must honor the 15-column
allowed-action mask. The worker verifies masks again before returning.

A small Rust generator eliminates manual duplication of the long model
signature. Place only the `models` array entries (as a top-level JSON
array) in `model-slots.json`, then run:
`cargo run --manifest-path native/Cargo.toml -p bees-host --example
policy_manifest -- model-slots.json native-models.json`. The generated
manifest includes the single source-of-truth v26 signature. Slot-relative
model paths are resolved relative to the generated manifest.
The generator also hashes each loaded ONNX/TorchScript file with SHA-256
and writes that digest into its model entry; the persistent Python worker
checks every digest before loading weights or acknowledging its model
handshake. If a checkpoint is replaced in place after manifest creation,
the worker fails closed instead of silently evaluating another network.
Hashes are computed only on startup/manifest creation, not during rollout.

Both processes must use matching artifacts. Diagnostics go to stderr;
stdout is exclusively binary protocol.

**Boundary:** Existing ML-Agents checkpoints have NOT been
verified/exported into a matching model interface here. This backend is not yet an operating
replacement for the original training system. Native PPO still requires
the genuine stochastic-action log probabilities, value estimates,
centralized group critic and completed rollout delivery. Inference
latency includes process transfer and GPU synchronization. A larger
native shared-memory/GPU execution path can be selected by the same
`NativePolicyInference` trait without changing gameplay.

## v26 batched policy interface

`bees-sim::policy_batch::PolicyBatch` provides a reusable, row-major
inference boundary for the exact v26 policy shape: 7,743 observation
floats, 16 continuous outputs and six discrete branches per agent.
A game-side observation encoder can fill a row **directly** using
`push_encoded` without constructing an intermediate 7,743-float vector;
each unwritten feature remains NaN and invalidates the row. Batches
preserve insertion order and exact world/agent **episode revision and
fixed-tick identities**, so an inference backend can share one invocation
across many worlds. `PolicyAgentKey::from_world` captures the identity;
`is_current_for` rejects delayed responses from retired episodes, advanced
ticks, or failed worlds before an action reaches gameplay.

The batch exposes contiguous output buffers for the actual model backend
and validates **every** returned action before publishing decisions:
finite continuous values, discrete branch ranges, available weapon slots,
and current executable special-action masks. Missing/partial outputs are
never silently replaced with zero actions. Both observations and output
storage retain capacity across decisions, and all action choices must be
provided by an actual model or explicit user control.
`PolicyBatch::footprint` reports current observation/action payload bytes
and retained numeric-buffer capacity without timing or extra copies. A
backend that submits asynchronous inference work must retain the batch
buffers until completion; `clear` is only safe once that work has finished.

The exact Unity `RlPolicySchema.Signature` and ABI version are available
through `validate_model_abi`, to reject incompatible checkpoints even
when their tensor dimensions happen to match. The native policy module
also ports the source-defined episode progress, signed faction indicator
and 19 reserved observation features; inactive trajectories use zeros
except for the faction discriminator.

The perception encoder below covers all 7,722 source-defined
perception features but still requires real Level data. `CombatWorld`
applies policy movement, allied communication, and **registered rotating
turret** aim/fire through its normal gameplay and physics update.
`bind_policy_ship` establishes the episode frame; then
`configure_policy_turret` registers each source-authored weapon slot
(`NativeTurretSpec`: mount pose, weapon/projectile type, power, range,
rate, rotation, projectile speed). The real `TurretState` and
`ScaledTimer` implement latched firing readiness at three targeting
passes per shot, and fired shots enter the canonical projectile collision,
damage, attribution and event pipeline. `policy_self_weapons` and
observed-ship summaries read those same physical mounts.
`policy_action_mask` enables firing only for registered supported slots;
missing mounts and unported subclasses/specials fail the full decision
batch before any motion or communications are applied.
`PolicyAgentKey::is_current_for` is still required at the inference host.

**Supported:** rotating turrets (LightCannon, Turret,
RocketTurret, Eye, QueenEye), DualCannon, LaserBuilder/SplitShot,
FullShipTurret, and BeamCannon using their authored projectile types.
`NativeFirePattern::DualCannon` requires the two authored barrel offsets;
each volley launches two HumanSmall shots with integer `Power / 2`, both
aimed from the ship center, each with its own shot-root ID.
LaserBuilder/SplitShot and FullShipTurret use their actual Unity clip
`Fire` animation event times (Leaf Cutter Shot Form: 0.8 seconds;
Flagship Laser Charging: 2.75 seconds), after the normal three targeting
passes. Shots remain queued after the policy stops requesting fire;
cease-fire prevents actual launch. FullShipTurret never turns the hull on
the policy's behalf and samples the current physical cannon heading at
the Fire event, not the earlier aim command. `policy_weapon_charge`
reports that same world-owned charge progress to a renderer.
BeamCannon creates just one active type-8 LaserBeam per mount. Its
authored prefab has Speed=0, a 2x2 BoxCollider2D and initial scale
(1, 0.5). After contact processing, each subsequent fixed update expands
the physical local-X half-width by 2, follows the real shooter/turret
heading and Rigidbody2D velocity, and ends once its previous half-width
reaches Range/2 or its shooter dies. The existing piercing contact ledger
spends half the contacted target's health as power budget, permits only
one accepted hit per ship, and releases BeamCannon ownership through
the common projectile-end path. A renderer and training worker both
observe the same projectile/weapon state.
**Striker Bomb policy special:** this is not a turret fire branch.
After `bind_policy_ship` and `set_ship_fleet_identity`,
`configure_policy_striker(striker_id, carrier_id, bomb_power)` binds the
real Striker (ShipTypes=19), its living same-side carrier, and authored
Bomb.Power. `policy_action_mask` enables ship-special choice 1 only
when loaded. A requested drop is resolved inside the shared fixed tick,
using actual authored ship collider overlap and the fixed-step collision
matrix; a distant enemy cannot be bombed. The created type-12
StrikerBomb uses the existing world-owned 1.5+0.5-second fuse,
target-lifecycle check, and snapshot shooter/fleet attribution. Loaded
state replenishes only on the recurring one-second check when the ship
returns within 15 units of its living carrier. Native contact tie-breaking
uses the lowest enemy runtime ID rather than Unity's last-entered
TouchingShip; the rendered bomb's random cosmetic attachment point also
remains a Level integration detail.

**Fire Barge explosion lifecycle:** gameplay setup registers source
Bomb.Power through `configure_fire_barge_bomb`, after assigning the
persistent FleetShip identity. Policy-controlled barges can also call
`configure_policy_fire_barge`, reusing the same registration.
`FireBarge.Kill` now creates the source-authored type-14 explosion
on either self-detonation or a lethal physical hit (projectile, Striker
bomb, contact bomber or asteroid). The one-shot registration is
consumed only after successful launch, preventing duplicate explosions;
the existing collider/timeline owns growth, friendly fire and expiry.
The two causes have separate counters, and the immutable projectile
owner preserves their correct RL source attribution. For gameplay
BombingRun, `queue_game_fire_barge_detonation(ship_id, command_outcome_id)`
records the actual command identity and applies the same explosion/death
transaction at the **next fixed combat step**; executing it inside the
command timer would bypass the completed tick's roster and rewards pass.
`select_native_game_bomb_target` now uses the real Bomb weapon ID and
`CombatWorld` damage-reservation ledger; its Closest/Furthest strategies
measure the imported target collider, not its center. A killed bomber's
weapon lease is released by the normal ship-retirement path.
The reusable `assign_initial_bombing_run_targets` operation now assigns
all squad Bomb targets in Unity member order. `NativeBombingRun` now coordinates authored Fire Barge, Yellow Jacket, and
Striker squads, including mixed squads. It preserves the original 2s/0.25s
polling, tracked pursuit, proximity-gated Fire Barge detonation, Striker
bomb drop/return/reload, and one shared Bomb reservation lifecycle.
The standard HiveMind director now installs this scoped controller via
`apply_strategy_receipt_with_bombing` when the owning Level supplies its
current roster, authored Bomb weapon IDs/power and GetShips-ordered enemies.
`NativeLevelCommandContext::bombing_command_snapshot` supplies actual
ProximityCollider membership only on due timer callbacks; the separate
`collect_bomber_contacts` callback supplies physical Yellow Jacket
trigger-enter contacts after the fixed combat step. Existing callers
without this input still explicitly discard unsupported BombingRun
reservations; no fictional range is substituted. Whole-squad initial
reservation assignments are atomic: failed replacement preserves the
outgoing command's leases. Completion, timeout, supersession and squad
retirement share the ordinary command lifecycle. Mixed squads and Charge are implemented in the command director but still
require a real Level adapter for authored carrier, weapon, eligibility and
contact inputs. Missing inputs fail closed. No measured throughput or
end-to-end gameplay-parity claim follows from these source ports.

`policy_special_measurements()` exposes bounded per-world counts of
validated special requests, Striker exact hull checks/contact misses,
real bombs/reloads, and Fire Barge detonations. Subtract snapshots to
profile expensive contact searches without per-frame logs or allocations.
The Striker contact path now rejects distant ship pairs using a
rotation-invariant bound computed from each ship's authored hull at spawn
or shape replacement, without suppressing close exact collider tests.
Separate distance-rejection and exact-test counters measure how much
polygon work this saves during real large-battle runs. A speedup has
not yet been benchmarked.

**Yellow Jacket ship special:** native ShipTypes=22 resolves a physical
enemy-hull contact and applies the authored Bomb.Power directly to the
enemy and reciprocally to itself. The two Bomb HitEvents carry zero
projectile/root IDs because Unity does not spawn a shot. The reciprocal
hit is marked self_inflicted for RL shaping, and the
YellowJacketDetonated event exposes the self-death cause even when
historical gameplay names the enemy as killer. Striker and Yellow Jacket
share the same exact, matrix-gated contact search and stable tie-breaking.
Their separate distance-rejection and exact-hull counters expose actual
collision work. Each agent now owns one tagged ship-special state rather
than independent optional Striker, Fire Barge and Yellow Jacket fields,
making unsupported combinations impossible by construction.
Scripted BombingRun shares this world-owned contact transaction; authentic
Level contact delivery, full command-stat persistence, and executable parity
qualification remain integration work.

**Barge v26 charge special:** a bound native Barge accepts a special only
when ready. The shared combat tick advances the authored 2-second windup,
1-second 80-speed forward charge and 10-second cooldown. Contact against
real enemy hulls applies immediate authored Charge.Power damage, 75%
reciprocal damage, and stops the active charge if the victim survives.
Charge completion inflicts the source's 200 self-damage through normal
unattributed health and death processing. The source's once-per-victim
charge contact memory is world-owned and cleared on cooldown or death.
Movement locks use the same ShipMotion, rather than a training-only
teleport or collision model. Active contact queries use the
same reusable physics grid and one buffer rather than scanning
the entire ship roster every tick. Since physics.step ends with its
obstacle query, the first active Barge lazily restores the ship grid
once for the tick; subsequent Barges share it. Zero Barges incur no
extra rebuild. A separate counter measures this rebuild overhead. Narrowphase checks still use exact
authored hulls and collision permissions in stable index order.
Separate broadphase/narrowphase counters measure candidate reduction;
no throughput improvement has yet been benchmarked. Charge damage
respects MakeShotsHarmless, matching Unity LogAttackingDamage.
The authored 13s cycle has a normalized time-until-ready observation.
Native Scout, Barge, Striker, Fire Barge and Yellow Jacket specials
now expose source-defined readiness, resource, cooldown and phase through
one world-owned observation snapshot. The self and capability projection
prefer this live state for native policy-bound agents, while non-policy
game controllers still supply their own authored fallback data. An
unconfigured native special remains masked instead of advertising
fictitious readiness. The native scripted Charge director shares these Barge phases, but still
requires authentic Level charging-range/visibility snapshots. Rendered
animation, presentation and executable parity qualification remain pending.

**Scout Beacon policy special:** the native policy Scout now supports
the source's five one-use Beacon drops with a ten-second scaled-game
cooldown and an optional RL per-side living-ship cap. The caller supplies
authored Beacon collider, health and motion (not guessed defaults).
Each temporary Beacon enters the *same* CombatWorld ship roster,
increments the live-side count, inherits the parent's persistent fleet
identity, and records its Scout parent for the Level/game adapter.
Special masks use an O(1) side count even in large battles; sorted policy
requests recheck the cap at execution so concurrent Scout requests
cannot exceed it. The same spawn/death boundary also maintains total
living-ship counts, eliminating the previous full-ship-array scan per
agent's v26 self-observation in large battles. Beacon spawning and cap denial have separate counters.
The graphical Level must still instantiate and register the Beacon's
minion Squad, presentation and autonomous orders using the spawn event;
these are not represented as magically complete in the native world.

**Not yet implemented:** a production Level adapter for the source command
snapshots and trigger events, complete Level-to-perception snapshot
collection, GPU inference, and an ML-Agents-compatible learner. Unity targeting timers
run on the Level's scaled update clock; this native implementation
currently advances the same timer semantics on the game fixed-step delta,
so live frame-scheduling parity still needs validation.

## Source-matched v26 perception encoder

`NativePerceptionWriter` now writes a **complete 7,743-float v26
observation row** into `PolicyBatch` with no intermediate row allocation.
Its sections follow `RlCombatPerception.cs`: self and capabilities,
parent carrier, 64 allies, 64 enemies, five self weapon mounts, eight mining
asteroids, 64 map objects, 48 moving collision asteroids, 16 reserved
objective channels, 21×21 local navigation occupancy, 16×16 team-shared
exploration freshness, and the episode/faction/reserved tail.
Per-world `EpisodeShipIdentities` generates stable permuted ship scalars;
`TeamExplorationGrid` tracks actual allied sight history; and
`LocalNavigationOccupancy` rasterizes Level borders and discovered
static obstacle bounds. Observation ordering, normalization, per-team
rotations, padding and weapon status channels follow the source code.
Reusable sorting scratch and compile-time grid rotations avoid per-feature
allocations. A few cumulative encoder counters describe how many source
entities actually occupied the policy slots. Failures are returned rather
than silently making a partial row usable for inference.

The `combat_perception_bridge` now reads live ship health, motion, side,
type and fleet identity directly from `CombatWorld`. It uses the world's
learned `LevelDiscovery` to select previously discovered enemies (including
those outside current sight), and real active static-collider bounds to
rasterize discovered obstacles. Learned collision asteroids also project
their current native collider bounds, rotation, velocity, and effective
health directly into v26 observations. Ordinary MapObjects are now
authoritative gameplay records paired with actual native obstacle colliders.
Projectile hits reduce their health, produce gameplay events and retire
their collider and faction discovery when destroyed. Learned MapObjects
use the primary Collider2D equivalent for vision and map-object observations.
Fire Tanks require explicit authored `CanisterBomb.Power` and optional
linked static-obstacle ID. A fatal projectile hit spawns the existing
type-15 explosion with inherited shooter/root-shot/command attribution;
the blast uses normal explosion damage, including friendly fire. Linked
static obstacles leave the physical world and learned discovery cache;
the `FireTankObstacleBrokenApart` event tells the renderer to create its
source-authored **cosmetic** debris and the Level to refresh static
navigation. Other linked obstacle types remain unsupported rather than
silently applying incorrect breakup behavior.

Team exploration reads living allied ship positions and requires actual
effective sight ranges. First-use ship
identity order is preserved by sorting live candidates before assigning
permuted episode IDs. `CombatWorld::refresh_hivemind_vision` now performs
source-matched real-collider closest-point sight checks for live enemy
ships and environment obstacles, using a cheap bounding-box rejection,
then writes first sightings to its faction discovery memory. It keeps a
separate per-side flag for **currently** visible enemies. The game supplies
the actual enabled/registered Hive Mind observer roster, effective sight
ranges and setup-suppression state; these cannot be inferred from hull
geometry. Learned memory survives observer exit/death, but actual ship and
collision-asteroid retirement forgets those targets.

For reproducible CPU vision profiling with authored ship collider geometry
on Windows or Linux:

```sh
cargo run --release --manifest-path native/Cargo.toml -p bees-sim --example vision_throughput -- --ships-per-side 32 --sweeps 250 --range 60
```

The benchmark reports sweeps/second, candidate pairs, bounds rejections,
exact collider tests, and newly learned enemies. Its initial placements
and sight ranges are synthetic; it is not a training benchmark.

**Integration boundary:** the native game/Level adapter must supply
the actual MapObject prefab geometry/layer on registration and the real
observer/weapon/special data, mining and other authored metadata, and
episode reward state. Native movement, allied communications and
source-supported rotating turret firing are connected; the remaining
weapon classes, specials, GPU inference and learner are not. This is
**not yet a working or parity-certified training loop**.
Rust tests and benchmarks have not been executed in this environment.

## Independent CPU rollout windows

`Simulation::run_parallel_window` advances each world through the **same**
`GameRules::step` as `Simulation::step_world`. Worlds are assigned to disjoint
CPU shards for multiple fixed steps per call; their state, RNG and
episode-local measurements are never shared. The single-world path remains
inline without launching threads. The caller supplies a policy/decision
callback and controls the number of CPU workers and the window length. The
callback reuses one action buffer per worker instead of allocating every tick.
The returned `ParallelWindow` includes real host wall elapsed and observed
world-steps/s, worker failure details, and summed physics measurements.
Individual failed worlds remain stopped until explicitly reset; panicking
workers invalidate the whole window.

This API is suitable for independent CPU inference or scripted rollout
windows where each arena's decisions can be generated locally. **Do not**
use it to invent surrogate neural decisions or force an externally batched
GPU policy into per-world synchronous calls. Batched GPU inference, rollout
transfer and PPO scheduling remain separate integration work.

## Cross-platform CPU throughput baseline

From the repository root, with Rust/Cargo installed on Windows or Linux:

```sh
cargo run --release --manifest-path native/Cargo.toml -p bees-sim --example rollout_throughput -- --worlds 16 --ships 2 --steps 1000 --workers 4
```

The CLI creates real authored Wasp/Gunship collider worlds, warms both
configurations, then compares the serial execution path against CPU-parallel
rollout windows. It emits CSV with observed wall seconds, world-steps/s and
the physics workload counters, so runs from different machines/configurations
can be compared. After each timed window, it compares the exact final
ship state, per-world RNG state, and physics-workload totals between
serial and parallel execution. This parity check is not included in the
reported elapsed time. Try larger `--ships` counts to measure arena-size scaling,
and adjust `--worlds` and `--workers` independently.

**Scope:** this is a movement/physics *baseline*, not a completed Bees game,
neural-inference benchmark or an end-to-end training measurement. The fixture
explicitly disables collision layers, firing, and AI because the authored
Unity collision-matrix and full game control path have not been integrated.
The production core retains its requirement for caller-supplied collision
rules; the benchmark does not change them.

## Current status

The portable `bees-sim` crate currently contains:

- Physics now culls off-grid bodies by exact cached AABB and keeps
  very wide authored colliders out of repeated multi-cell insertion.
  The normal world/host telemetry separately reports ship-grid and
  obstacle-grid overflow-body work, alongside broadphase candidates
  and narrowphase tests. These are algorithmic savings, not yet
  empirically measured speedups.
- A fixed-step multi-world dispatcher, per-world deterministic RNG, explicit
  episode reset and sampled timing/failure counters.
- The v26 policy's observation/action *shape* and actual action decoding:
  distinct team coordinate frames, dead zones, held turret aim, healing/fire
  exclusion and clamped communications.
- The existing RL-controlled ship motion algorithm: heading calculation,
  unrestricted timed turning, temporary current-speed behavior, stop sentinel
  and explicit hull-turn propagation to attached turret state.
- Base turret RL aim, three-pass readiness latch, authored cooldown callback
  contract, and firing eligibility.
- Projectile velocity, strict range expiry and continuation/command attribution.
- The reusable ScaledTimer state machine, including immediate execution and
  generation-preserving callback reuse semantics.
- Versioned native assets for **all 24 primary ship colliders**, migrated from
  their specific serialized `Entity.Collider` component references. The same
  collision catalog is loaded once per process and shared across worlds.
- A selected Unity Stage can now export its exact ordered normal
  CollisionAsteroid prefab list and source `AsteroidMaxSpeed` via
  **Bees → Native → Export Selected Stage Asteroid Palette**. The
  `FullAsteroidStage::from_unity_export` loader verifies Stage name,
  speed and prefab identities instead of assuming every map spawns all
  28 known asteroid variants. Duplicate prefab entries retain their
  authored selection weighting; each pooled profile retains its
  Create-time speed. Export per authored Stage as applicable.
- `bees-host::scene_assets::NativeSceneAssets::load` now reads both
  Unity-authored exports once and creates the shared native
  `NativeLevelRules` plus source-validated ordinary asteroid pool setup.
  Every world reuses the immutable collider catalog and physics matrix;
  each keeps its own active asteroid instances and sampled diagnostics.
  If either asset is missing or mismatched, Level construction fails
  rather than running silently with invented collisions or spawn weights.
- The native collision-layer matrix can now be exported directly from the
  authoritative Unity Editor via **Bees → Native → Export Physics2D Collision
  Matrix**. The generated `native/assets/unity-physics2d-matrix.json` contains
  the complete 32×32 permission matrix; `CollisionMatrix::from_unity_export`
  validates its version, dimensions and symmetry. The native game/worker
  must explicitly load this exported asset when constructing Level rules:
  there is no guessed all-colliding or all-blocked production default.
- Polygon, circular and box hulls (including concave polygon contact);
  reusable broadphase spatial grid; kinematic velocity integration; explicit
  layer-mask filtering; trigger-entry detection and per-step physics counters.
- Source-matched direct-projectile and explosion-contact eligibility rules,
  plus shared piercing/beam power budgets, Splitter Shot fragment generation,
  rocket acceleration and explosion hit tracking.
- A versioned asset catalog for **all 15 projectile types**, covering circles,
  rotated boxes, stationary explosions and the script-driven (non-colliding)
  Striker Bomb. The importer excludes visual-only impact animations.
- A shared, cached world-collider path for both ship and projectile geometry,
  including polygon-to-box and box-to-box trigger checks.
- A physics-only world adapter wired into the shared scheduler. It explicitly
  rejects PPO actions until the complete gameplay rules are migrated.
- A partial shared combat-world impact path with projectile trigger queues,
  health and TSV recalculation, shooter/Hive Mind attribution, death events,
  and source-consistent shot expiry and cleanup. Frigate rockets now accelerate
  with their authored speed cap and create untargeted explosions that inherit
  root-shot/command attribution and ownership without applying direct damage.
- Outgoing target-damage reservations use a single owner per reservation:
  weapon targeting/firing reserves once, projectile expiry releases once, and
  a rocket transfers its reservation to the resulting explosion before dying.
  Reservation totals continue to update with target health.
- Ended projectile physical/metadata storage is reused, so its capacity tracks
  peak concurrent shots instead of increasing across the entire run. World
  entity IDs remain monotonically increasing independently of pooled slots.
  Ships and shots spawn through the same authored collider catalogs in
  rendered/headless contexts.
- Striker Bomb's two-stage fuse, original target/shooter identity checks and
  target-relative attachment are implemented in the shared combat world.
- Frigate, Fire Barge and Fire Tank explosion animation events are imported
  from their Unity clips. Their radius changes modify the actual physics
  collider; harmlessness disables new damage; Kill releases the reservation
  and returns the projectile slot to the pool.
- Leafcutter SplitterShot now uses its authored four-way split, BeeSmall
  child speed, original shot/command attribution and untargeted child
  reservation semantics. Children spawn before a valid parent impact
  finishes; map-border contacts and ordinary range expiry do not split.
- Normal CollisionAsteroid creation and reuse now have a shared authored
  spawn-motion path: pool-owned prefab/speed, exact source-style off-map
  edge sampling, interior destination retry budget, and velocity/spin
  assignment. The Stage's authored prefab palette and reusable normal
  asteroid profiles are now owned by CombatWorld, independent from shard
  profiles, while physical obstacle slots remain recycled separately.
  World.RandomStream owns all draws, and compact per-world counters report
  new/reused profiles and interior placement draws. The source RNG streams
  remain numerically different from Unity.
- Obstacle bodies now participate in the same kinematic physics as ships
  and projectiles, with a separate, layer-filtered obstacle trigger queue.
  Ordinary projectile impacts can damage collision asteroids, terminate
  against borders or record static-obstacle hits; rockets explode on
  non-border obstacles and explosion projectiles deduplicate asteroid hits.
  All **28 authored collision asteroid variants** are imported with both
  primary and child proximity polygons (including inherited vertex
  overrides, transform scale, size class, and separate collision layers). A
  second catalog provides **eight static box variants and four map borders**
  with authored dimensions and prefab-default poses, while a third imports the
  standalone **208-vertex Polygon Obstacle** gameplay collider (excluding its
  separate proximity sensor). Destruction events
  include Unity's size-class-dependent shard and piece counts. Destroyed
  collision asteroids leave the active ID registry and their inactive
  physics/metadata slots are recycled on subsequent spawns, with new entity
  IDs and world-local reuse metrics. End-of-level destruction suppresses
  fragment events. The shared physics now reports separate primary-hull
  and authored-proximity entry/exit contact streams, including directed
  asteroid-to-obstacle proximity contacts. Map borders track asteroid
  entry/exit history and remove exiting asteroids without fragmentation.
  The native asset catalog preserves authored OriginalHealth separately
  from an episode's effective training durability. The shared combat world
  now uses proximity and primary contacts for asteroid/asteroid collisions
  and asteroid/ship damage, with Unity's size comparison, health accounting
  and 0.35-second delayed destruction. Ship contacts require explicit
  authored SizeClass and OriginalHealth (never inferred from adjusted
  training durability); original health comes from server-loaded ship stats
  and SizeClass can legitimately be zero (Beacon and Human Target).
  Missing profiles fail instead of applying guessed damage. Trigger ordering
  still requires comparison with Unity. Configured arenas now use the exact
  RL scene's 13 shard-prefab and 18 debris-prefab palettes. Destroyed
  asteroids spawn real shared-physics shard entities with original size/
  collider shapes, per-prefab training health, inherited angular velocity,
  movement, authored border-entry flags and sibling collision exclusions.
  The dedicated shard pool retains prefab/speed across reuse; cosmetic pieces
  have pooled, non-colliding motion and timed fade/expiry state. All fragment
  draws use the scheduler's seeded world-local RNG. Map/pathfinder invalidation,
  Unity-exact random sequences/callback ordering, renderer integration and
  complete observer cleanup remain unimplemented.
- An optional tactical reward consumer using the current RL team's shared
  positive/negative asymptotic shaping budgets, with mined cargo excluded
  from persistent-fleet damage reward on death.
- A separate, optional native episode-terminal controller uses source
  GameState.IsSideKilled mobile-roster counts, squad-death-triggered
  elimination and elapsed simulated time. Immobile Beacons/targets do not
  prevent elimination; timeout remains an unconditional draw. No game or
  training runner is entitled to infer a terminal winner from TSV.
  Full terminal rewards/MA-POCA delivery remain incomplete.

**Not yet implemented:** complete normal user/Hive Mind movement,
weapon-specific targeting and projectile spawning, remaining authored
static/border/mining obstacle prefabs and the full collision-layer matrix,
moving-asteroid AI, final ship collision-profile wiring from game assets,
other level-specific fragment palettes, complete map/pathfinder/observer
registry cleanup,
Unity-equivalent Animator/FixedUpdate/Physics2D ordering, native Hive Mind
command updates and persistence, squad/formation removal and full ship-death
lifecycle, complete RL reward delivery and episode termination, visibility
and observations, game modes, presentation, server/persistence, distributed
PPO and experiment orchestration.

The current native physics is a **partial implementation**, not a valid
standalone Bees battle. It must not produce training or evaluation samples
until full gameplay parity is achieved. Source tests and collision models
do not imply that Unity/Box2D physics parity is already established.

## Source-of-truth porting map

| Gameplay or service contract | Existing authoritative source |
| --- | --- |
| Authored primary ship hull assets | `Prefabs/Entities/Ships/*.prefab` via serialized `Entity.Collider` |
| Core geometry, timing and body orientation | `Scripts/Entities/Entity.cs`, `Scripts/Utilities.cs`, `Scripts/ScaledTimer.cs` |
| Base turret aim, targeting and shot cadence | `Scripts/Entities/Ships/Weapons/Turret*.cs` |
| Shared health, value and hit accounting | `Scripts/Entities/Ships/Ship.Combat.cs`, `Scripts/Utilities.cs` |
| Ported tactical reward budget | `Scripts/Scenes/RlOneVsOneReward.cs`, `RlOneVsOneEpisodeCoordinator.cs` |
| Ships, movement and subclass behavior | `Scripts/Entities/Ships/` |
| Weapons, projectiles, damage and collisions | `Scripts/Entities/Ships/Weapons/`, `Scripts/Entities/Projectiles/`, `Scripts/Entities/` |
| Scenes, game modes, map generation and UI | `Scripts/Scenes/`, `Scripts/Levels/`, `Scripts/UI Components/` |
| RL policy v26 and perceptions | `Scripts/Scenes/RlPolicySchema.cs`, `RlCombatPerception.cs`, `RlOneVsOneAgent.cs` |
| Rewards, visibility, episode termination | `Scripts/Scenes/RlOneVsOneEpisodeCoordinator.cs`, `RlOneVsOneReward.cs` |
| PPO/MA-POCA and distributed actors | `Training/bees_mlagents_structured_policy.py`, `bees_mlagents_ppo_compat.py`, `bees_wan_actor_worker.py` |
| Experiment scheduling and curriculum | `Training/bees_experiments.js`, `Training/experiment-curriculum.json` |
| Authentication, strategy learning and persistence | `BeesServer~/`, repository `allStackGit/BeesServer` |

The first end-to-end executable milestone must have both a rendered and
headless mode linked to the **same** actual combat implementation. Existing
runtime and experimental branches remain the reference until replacement
parity, cross-platform operation and performance are demonstrated.

## Build interface

From `native/`, when a Rust toolchain is installed, run
`cargo test --workspace` and `cargo check --workspace --all-targets`.
The simulation runtime is independent of Unity and Python; its immutable
asset catalog uses serde/serde_json once during initialization. The importer
is a separate, optional Node script for the migration period. These commands
exercise the library source and unit contracts; they do **not** prove game,
physics, model or distributed-system parity.

Every native gameplay feature must be implemented once in the shared core.
Rendering and learning modules must call that implementation rather than
copying its rules into platform-specific or training-only code.

## Authored collider migration

`native/assets/ship-colliders.json` is the versioned **native** source for
primary ship hulls. To regenerate it from current Unity source during migration,
run from a checkout of this repository:

```sh
node native/tools/import-unity-ship-colliders.mjs
```

The importer resolves the explicit root `Entity.Collider` file ID, including
negative IDs, and distinguishes the hull from attached sensing/range colliders.
It incorporates authored XY transform scale (the Human Target prefab uses
50×) and rejects unsupported shapes or ambiguous layouts. Once Unity assets
are retired, the native catalog remains authoritative and should be maintained
through native asset tooling rather than depending on Unity at runtime.

The physics layer accepts a required collision-layer matrix, but no verified
export of the existing Unity Physics2D matrix is yet available. Do not replace
this missing contract with an allow-all default in gameplay or training.

## Local native verification

From the repository root, with Node.js and a Rust/Cargo toolchain installed:

```sh
node native/tools/verify-native.mjs
```

This checks the six authored asset catalogs against the current Unity
prefabs **without rewriting them**, then compiles and runs the native Rust
tests. An optional `--assets-only` skips Cargo explicitly and does **not**
establish native compilation. These checks establish neither Unity
Physics2D parity nor complete battle readiness.

## Collision asteroid prefab migration

`native/assets/asteroid-colliders.json` (schema 3) contains all three
authored polygons for each of the 28 `Collision Asteroids/Asteroid N.prefab`
variants: primary damage hull (layer 19), nearby-entity proximity trigger
(layer 20), and separate navigation clearance mapping (layer 26).
Standalone prefabs and inherited Unity YAML overrides are decoded by a
single source importer:

```sh
node native/tools/import-unity-asteroid-colliders.mjs --check
```

Omit `--check` to regenerate. The importer uses the serialized
`Obstacle.Collider` reference and **honors the declared path-array size**:
serialized point overrides for inactive paths are not collision geometry.
It rejects unsupported multiple *active* polygon paths in any collider.
Proximity and clearance each apply their own authored child scale/offset
and layer, rather than scaling the primary hull by a guessed radius.
Navigation geometry is updated from the same obstacle pose as the combat
hull but is never included in damage/proximity contact streams.
`ObstacleBody.navigation_bounds`, `navigation_contains_point` and
`navigation_closest_point` expose the data used by Unity's
`Pathfinder.GetObstaclePoints`, and
`CombatWorld.dynamic_navigation_asteroids` enumerates the currently
live asteroids rather than keeping a stale pathfinder registration list.
The separate `NavigationGrid` now rasterizes live dynamic asteroids into
a caller-owned blocked-cell mask using Unity's four-unit grid scale,
2.5-second absolute-velocity bounds padding, five point-overlap probes,
closest-point contact and projected-velocity probe. Its explicit map-space
conversion callbacks preserve the Level's local/world transform boundary.
The `NavigationClearance` and `NavigationSearch` modules now consume this
same occupancy rasterizer for static obstacle masks and moving asteroids.
They reproduce Unity's two-pass clearance distances, signed-static egress
map, preferred-clearance penalties, eight-neighbor A* with diagonal
corner checks, static-obstacle egress, and route smoothing. Per-search
arrays and heap storage are reusable, with route points returned in
Level-local grid coordinates. The destination-clearance check and
nearest-walkable ring search are also available; the Level retains
responsibility for its exact ForceBounds behavior.

`NavigationClearance.are_statically_connected` caches source-equivalent
static connectivity regions by ship clearance. It permits blocked starts
through to static-egress recovery and clears cached components when static
geometry changes.

`CombatWorld` owns per-ship `NavigationRequests`: repeated requests
coalesce, changed destinations invalidate existing work, and old search
results are rejected by request and ship-lifecycle identity. A superseded
running job requeues only the most recent destination, using the ship's
current position once its prior worker completes. Invalidating an active
result preserves its occupied worker slot until it actually finishes.
The reusable `NavigationRuntime` owns the Level map's full affine transform,
static/dynamic clearance maps and A* arrays. It refreshes directly from
`CombatWorld`'s live obstacles; `refresh_world_navigation` and
`solve_next_world_navigation_job` provide the shared integration boundary.
For interactive native clients, `NavigationScheduler::pump` now connects
these APIs to bounded background CPU workers. Each worker reuses its own A*
arrays; a per-fixed-frame immutable clearance snapshot is shared by Arc.
Results are applied only on the world-owning thread. Pending static-layer
rebuilds wait until active searches finish, while newer dynamic-asteroid
snapshots can coexist with older in-flight searches. Worker shutdown drains
owned jobs without leaving the channel receiver blocked. The five-second
Unity search deadline is enforced only for these interactive workers, not
the deterministic synchronous/headless search API.

For normal gameplay, callers supply a `GameMovementProfile` with the
actual configured `ShipTurningRadius` and rendered ship half-extents.
Then `move_ship_to_point` applies the Level's explicit bounds
and collision-asteroid activation settings, sweeps the source-sized rotated
BoxCast against real layer-19 polygon/circle colliders, and chooses direct
movement or A*. Movement locks preserve the latest order and reevaluate
the actual obstacle geometry on unlock. `move_ship_direct`,
`move_ship_in_direction`, and accepted routes use `GameShipSteering`
in the same pre-physics step as RL-controlled `ShipMotion`. Failed routes
use Unity's five attempts separated by two-second timers; asteroid proximity
entries trigger replanning and recurring one-second nearby-asteroid checks
do not invalidate active workers. Route waypoints, egress counts, turning
dead bands, variable speed and StopMoving share the existing physics core.

**Integration still incomplete:** squad/Hive Mind/user command
dispatch must supply authored movement profiles and Level orders, and the
native game loop must call the path scheduler at the appropriate fixed-frame
boundary. The native sweep uses continuous authored-shape geometry but does
not promise Box2D's precise hit ordering or Unity's 16-hit non-alloc buffer.
Multi-map placement and gameplay/rendering parity remain unverified.
As requested, these changes have not been compiled or executed.

The catalog stores normal `SizeClass × CollisionAsteroidHealthIncrement` durability;
training must still pass its effective-health override when spawning, while
collision rules retain the original authored health.

## Fragment prefab palettes

The RL training scene's serialized `BreakawayAsteroids` and
`AsteroidPieces` GUID lists are imported to
`native/assets/asteroid-fragments.json`. To verify or regenerate them:

```sh
node native/tools/import-unity-asteroid-fragments.mjs --check
```

The shared `AsteroidFragmentPalette` preserves the scene's exact selection
list rather than choosing arbitrary small asteroid shapes. Set
`CombatWorld.configure_asteroid_fragments` with the game Level's map bounds,
half-extents, Stage maximum asteroid speed, and optional RL health ratio.
The scheduler automatically passes its world-local RNG through
`CombatWorldRules`; direct `step_fixed` deliberately rejects a configured
fragment world unless the caller uses `step_fixed_with_random`. Debris-piece
fade indexes are available to the future renderer; no independent training
fragment simulation exists. Reproducing historical Unity `.NET Random`
draw sequences and cross-episode pool persistence remains outstanding.

## Static boxes and map borders

`native/assets/box-obstacle-colliders.json` records the directly inherited
`_Obstacle` box variants, the two second-level `Slanted Variant` and `Box`
prefabs, and all four authored map-border variants. To verify
against the exact prefab source revisions:

```sh
node native/tools/import-unity-box-obstacle-colliders.mjs --check
```

Omit `--check` to regenerate. Static/border boxes can be spawned by name
through `BoxObstacleCatalog` and `CombatWorld.spawn_box_obstacle`, with an
optional map-specific pose. The imported box dimensions reflect prefab
scaling; **runtime map-dependent resizing is not yet integrated**. Other
obstacle layouts and unrecognized prefab inheritance chains remain outside
this catalog until their relevant gameplay colliders are traced.

## Standalone polygon obstacle

`native/assets/polygon-obstacle-colliders.json` is generated from the
standalone `Polygon Obstacle.prefab`:

```sh
node native/tools/import-unity-polygon-obstacle.mjs --check
```

The import follows `Obstacle.Collider`, not `ProximityCollider`, and
retains the entire concave primary polygon and the authored root scaling.
`PolygonObstacleCatalog` and `CombatWorld.spawn_polygon_obstacle`
share that geometry between rendering and training.

## Projectile collider migration

`native/assets/projectile-colliders.json` records all 15 non-None
`ConfigData.ProjectileTypes`. Its source importer is:

```sh
node native/tools/import-unity-projectile-colliders.mjs --check
```

Omit `--check` to regenerate from Unity prefabs. Both importers use an explicit
serialized collider reference and reject shape assumptions. These are transition
tools, not production runtime dependencies.

Frigate Rocket speed is exported from the prefab's `MaxSpeed` (160);
the explosion's authored collider is loaded from projectile type 13. During
migration, the native combat world requires one shared immutable
`Arc<ProjectileAssetCatalog>` at construction. Explosion event callbacks now run in the native
simulation, but their ordering against Unity Animator and Physics2D ticks
has not been verified in a running reference build.

The runtime shares one transformed-collider implementation between ships and
projectiles. Source-based shape geometry is necessary but **does not establish
Unity Physics2D callback/timestep parity**; validating that will require the
full gameplay integration and the original runtime.
