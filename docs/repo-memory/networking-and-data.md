# Networking and data

## Socket recovery

- Automatic client reconnect already belongs to `Scene`: while `Socket.HasClosed` and this scene owns socket management, it retries through `ConfigData.RetryConnection()` on a 10-second timer.
- `Socket.Open()` handles successful recovery by clearing the closed state and sending `ReconnectLevelRequest` for open levels.
- Standing request resends must run only while the socket is actually open. Resending into a closed WebSocket during reconnect creates churn/errors and competes with recovery.
- Intentional shutdown (`KeepClosed`) must suppress automatic reconnect.
- BeesServer can temporarily reject reconnects while its consolidation queue is non-empty after the last client disconnects. Recovery therefore needs repeated attempts even when the Node process itself never crashed.
- No protocol ping/pong heartbeat is currently part of this recovery contract; add one only if runtime evidence shows transport-idle detection needs it.

## Persisted identity/schema

- Persisted level lookup must use `LevelOptions.Id`, not list position. Server ordering can change and IDs can be sparse.
- New persisted IDs must be allocated from existing ID values rather than collection count for the same reason.
- User settings are a migratable schema: when new controls/hotkeys are added, merge missing defaults into older saved settings rather than requiring a clean profile.
- Fleet/ship/squad IDs are intended to be globally unique across modes; do not infer identity from collection position or display text.
- Negative FleetShip IDs belong to transient/unsaved ships and may not exist in `ConfigData.CurrentShips`. If code intentionally creates a transient `FleetShip` with metadata such as a copied name, the owning `SquadShip` must retain/cache that FleetShip object rather than discard it and later reconstruct a different object from only ID/type.
- Saved-squad membership is runtime state on each `FleetShip`. Removing a persisted squad must release `DoesBelongToSavedSquad` for every member immediately; otherwise those ships remain unavailable until a reload reconstructs membership.
- Temporary child squads/ships may deliberately share a parent `SavedSquad`/`FleetShip` for identity or stat accounting. Only the primary persisted squad/ship owns `IsLoadedIntoLevel`; child teardown must not mark the parent unloaded.
- Reinforcement composition counts describe ships that can actually spawn. `Ships.GetSquadByComposition()` must compare requested counts against `GetAliveSquadShips().Count`, not total persisted slots that can include dead FleetShips.
- `ConfigData.Version` is part of the server/database settings contract. Version changes require matching server rows whose serialized shape matches the client expectation.
- Server configuration values used by gameplay must be assigned in `Configuration.ProcessData()` rather than silently falling back to field initializers. The active version-5 configuration currently supplies `AIRandomMovementMaxDistance` (200), so AI movement/scouting radius is a server-owned tuning value rather than the local fallback 256.
- Integration/test server operation must use the test database rather than live data.

## Save boundaries

- Low-level `SaveSquadData`/`SaveFleetData` are also used by automatic campaign persistence. User-facing save audio/feedback belongs at explicit UI save-completion boundaries, not inside the generic persistence calls.
- Sprite-cache writes must finish before `HasCachedSprite` is marked. However, that flag is coarse FleetShip readiness, while cache files are keyed by ship type, squad color and sprite index; a missing/corrupt individual cache entry is therefore a valid cache miss and must fall back to live recoloring rather than make rendering fail.
