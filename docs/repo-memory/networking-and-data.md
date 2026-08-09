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
- `ConfigData.Version` is part of the server/database settings contract. Version changes require matching server rows whose serialized shape matches the client expectation.
- Integration/test server operation must use the test database rather than live data.

## Save boundaries

- Low-level `SaveSquadData`/`SaveFleetData` are also used by automatic campaign persistence. User-facing save audio/feedback belongs at explicit UI save-completion boundaries, not inside the generic persistence calls.
- Sprite-cache readiness must only be marked after the cache file write has completed; callers use that flag as a real availability contract.
