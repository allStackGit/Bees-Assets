# Bug Ledger

### BUG-001 — StoreCommands cannot preserve issued outcomes across a server restart
**Location:** `Scripts/Server/ServerStoredCommand.cs`, `Scripts/Server/StoreCommands.cs`, `Scripts/Levels/GameState.Commands.cs`; cross-repository BeesServer persistence contract  
**Description:** The client retains finalized Hive Mind commands and can resend them after reconnect, but each StoreCommands item serializes only `OutcomeId` and TSV. The server keeps the corresponding matchup/strategy/table reservation metadata only in memory, so a server restart destroys the information required to persist outcomes that were legitimately issued before the restart. The current server silently acknowledges those unmatched IDs without storing them. The reconnect/persistence protocol must distinguish or recover stale reservations rather than lose valid training data while reporting success.

### BUG-002 — Successful server writes are classified as failures and resent forever
**Location:** `Scripts/Server/SocketResponseLifecycleGuard.cs`, `ShouldKeepWriteRequestPending`; BeesServer write response contract  
**Description:** The guard treats a basic write as successful only when `response.Status == 1`, but BeesServer sends `Status: 200` for successful `store-commands` and `store-user-data` responses. The guard therefore consumes every successful write response before `Socket.HandleBasicResponse` can retire it, leaves the request in `StandingRequests`, and the normal timeout logic resends the already-successful write indefinitely. This causes repeated user-data writes and perpetual StoreCommands retries and can amplify load dramatically during training.

### BUG-003 — Production client traffic is sent over unencrypted WebSocket
**Location:** `Scripts/Server/Socket.cs`, `Protocol`, `IsSecured`, constructor URL construction  
**Description:** The production socket URL is always constructed with `Protocol = "ws"`; `IsSecured` is never enabled anywhere in authored code, and it only changes the WebSocketSharp TLS options rather than the URL scheme. Server-backed player data therefore travels over an unencrypted WebSocket on the production path. This also prevents safely transporting a reusable Steam authentication ticket needed to bind the connection to the player's identity. Production must use `wss://` with certificate validation while retaining an explicit insecure mode only for local/test use.
