# Bug Ledger

Only defects validated by static code tracing are recorded here. No tests, builds, executables, benchmarks, simulations, or GitHub Actions were run.

### BUG-001 — Synchronous WebSocketSharp I/O can freeze the Unity main thread
**Location:** `Scripts/Server/Socket.cs`, `MakeSocket()` / `Send()`; `Scripts/Scenes/Scene.cs`, automatic reconnect path  
**Description:** The active transport is WebSocketSharp. `Socket.MakeSocket()` calls its synchronous `Connect()` both at initial construction and from `Scene.AutomaticConnectionRetry()` through `ConfigData.RetryConnection()`, while `Socket.Send()` calls synchronous `Send()` from Unity-side request flows. WebSocketSharp performs blocking handshake/stream I/O in those methods (including a long handshake timeout). A slow/unreachable server or blocked write therefore executes network I/O on Unity's main thread and can halt rendering and input until the call returns.

### BUG-002 — Response backlog processing is unbounded on the Unity main thread
**Location:** `Scripts/Server/SocketResponseLifecycleGuard.cs`, `FilterFailedResponses()`; `Scripts/Server/Socket.cs`, `Update()`  
**Description:** The lifecycle guard runs every rendered frame and drains the entire concurrent `Socket.MessageQueue`, filters it, then re-enqueues every accepted response. `Socket.Update()` later drains that same queue with another unbounded `while (TryDequeue)` loop. A large or continuously replenished response backlog can therefore keep either main-thread loop running for an arbitrarily long time, starving rendering/input; accepted responses are also repeatedly scanned by the guard until the next socket dispatch tick.

### BUG-003 — Authentication failures can leave startup requests retrying forever with the same rejected ticket
**Location:** `Scripts/Server/SocketResponseLifecycleGuard.cs`, `ShouldSuppressResponse()`; `Scripts/Server/ServerRequest.cs`; `Scripts/Server/GetUserData.cs`; `Scripts/Server/GetUserSettingsData.cs`; `Scripts/Server/SteamWebApiAuth.cs`  
**Description:** HTTP-style 401 responses for settings/user-data and typed requests are suppressed as retryable while their standing request remains. Requests have no total resend limit, and their `AuthTicket` is captured as a readonly value when the request is constructed. Nothing on 401 resets/renews the Steam Web API ticket, so the same rejected credential is resent indefinitely while the WebSocket remains open. During bootstrap, settings/user data never finish loading, the scene never finalizes, and the disconnect UI never appears because transport connectivity itself is still healthy.

### BUG-004 — A pending profile checkpoint retries every frame while disconnected and can overwhelm the game
**Location:** `Scripts/CampaignCheckpoint.cs`, `FlushIfReady()`; `Scripts/Server/SocketResponseLifecycleGuard.cs`, `Update()`; `Scripts/Server/Socket.cs`, `SendRequest()` / `Send()`  
**Description:** `CampaignCheckpoint.Save()` leaves `_pendingSave` true until `FlushIfReady()` successfully calls `Socket.SendRequest()`. The persistent lifecycle guard calls `FlushIfReady()` every rendered frame, but readiness does not require an open socket. If a profile save is pending while WebSocketSharp is closed, each frame reserializes all seven profile files, creates/logs another standing request, then synchronous `Send()` throws because the transport is not open before `_pendingSave` can clear. The next frame repeats the full allocation/log/request path, rapidly growing pending requests and main-thread load until the game can effectively freeze.

### BUG-005 — Full Retreat can remain command-locked forever when the Warp Gate is unreachable
**Location:** `Scripts/Levels/Commands/FullRetreat.cs`, `Execute()` / `MoveToWarpGate()`; `Scripts/Entities/Ships/Ship.Movement.cs`, failed-path retry lifecycle  
**Description:** Ship pathfinding gives up after five failed safe-path retries and stops moving, but `FullRetreat` has no `TimeoutTimer`. Its recurring command timer continues issuing the same Warp Gate destination indefinitely while `_shipIdsWarping` remains non-empty. If surviving participants cannot reach a live gate, the squad never finalizes the command and remains permanently command-locked. Other movement commands use `StandardMaxCommandTime` to guarantee eventual finalization.
