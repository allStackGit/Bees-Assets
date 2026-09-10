# Networking / Server / WebGL Route

Load this route only when a focused socket/server/WebGL task needs contract context beyond exact source/tests. Inspect BeesServer as well before changing a wire, persistence, reconnect, learning-key, or shared identity contract.

| Concern | Start with | Important boundary / evidence |
|---|---|---|
| Hive Mind command request / matchup | `Squad.Commands`, `Socket`, `CommandRequest`, `MatchupStrategyRequest` | acting/enemy/ally composition; availability participates in server cache identity |
| shared server Game / concurrent Levels | Unity `SetupLevelRequest`, `ReconnectLevelRequest`, `Level.ServerGameId` | one WebSocket can share one server Game; Level remains client ownership unit |
| response lifecycle / stale pooled squad | `SocketResponseLifecycleGuard`, `StandingRequestSet`, `Socket` | captured pooled lifetime; request hash; handled-history bounds; terminal statuses |
| reconnect / auth / resend | `Socket`, `SteamWebApiAuth`, standing requests, `OpenLevels` | auth refresh; stale-generation suppression; main-thread dispatch |
| WebGL browser WebSocket | `Plugins/WebSocket.jslib`, `Plugins/WebSocket.cs`, `WebSocketFactory` | browser callback to managed bridge; Development WebGL evidence |
| WebGL IL2CPP request tracking | `ServerRequestSet`, `Socket.LogRequest`, waitable requests | request `Hash` is transport identity; avoid unsupported comparer/interface-dispatch patterns |
| secure socket initialization | secure `Socket` constructor, `SecureSocketFactory` | never allocate runtime socket without constructor/field initialization |
| WebGL AOT JSON | `AotJson`, startup/settings/profile JSON paths | avoid unsupported runtime binder/dynamic paths; verify current regression tests |

Use `docs/engineering/REGRESSIONS.md` only when a specific known WebGL/network regression is implicated; do not preload the full history.
