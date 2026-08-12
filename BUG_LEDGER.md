# Bug Ledger

### BUG-001 — StoreCommands cannot preserve issued outcomes across a server restart
**Location:** `Scripts/Server/ServerStoredCommand.cs`, `Scripts/Server/StoreCommands.cs`, `Scripts/Levels/GameState.Commands.cs`; cross-repository BeesServer persistence contract  
**Description:** The client retains finalized Hive Mind commands and can resend them after reconnect, but each StoreCommands item serializes only `OutcomeId` and TSV. The server keeps the corresponding matchup/strategy/table reservation metadata only in memory, so a server restart destroys the information required to persist outcomes that were legitimately issued before the restart. The current server silently acknowledges those unmatched IDs without storing them. The reconnect/persistence protocol must distinguish or recover stale reservations rather than lose valid training data while reporting success.
