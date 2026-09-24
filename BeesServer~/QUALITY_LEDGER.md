# Quality Ledger

Current unresolved high-value maintainability/clarity debt found by touched-code quality review. This is not a style wishlist or history. Bugs belong in `BUG_LEDGER.md`; performance opportunities belong in `PERFORMANCE_LEDGER.md`.

### QUAL-001 — Remove tracked `node_modules`
**Location:** repository `node_modules/` and `.gitignore`  
**Problem:** dependency installation output is tracked even though `.gitignore` explicitly excludes `/node_modules`. This substantially increases repository/search volume, duplicates lockfile-owned dependency state, and can cause agents to inspect vendored dependency code when looking for first-party behavior.  
**Improvement:** remove tracked `node_modules` from Git while retaining `package.json`/`package-lock.json` as the dependency source of truth; reinstall locally as needed.  
**Why deferred:** this seeding pass changes only maintained agent knowledge and documentation; deleting a large tracked tree should be an explicit repository-cleanup change.

### QUAL-002 — Reduce production dependence on source-text rewriting of the legacy monolith
**Location:** `siServerDev.js`, `server.js` legacy-source transformations  
**Problem:** the active modular runtime still obtains core `User`/`Game`/strategy classes from a large legacy monolith and rewrites exact source fragments before executing them in `node:vm`. The fail-closed markers are safer than silent drift, but ordinary edits to the monolith can break production integration for text-shape reasons and force agents to understand both historical and modular behavior simultaneously.  
**Improvement:** incrementally extract stable domain/strategy classes behind explicit modules/interfaces while preserving numeric strategy IDs, request semantics and existing transformation-contract tests until each marker is no longer needed.  
**Why deferred:** this is a broad architectural migration across learning-history and protocol compatibility surfaces; it requires staged behavior tests and cannot be safely folded into a documentation-only pass.

### QUAL-003 — Consolidate the consolidation implementation boundary
**Location:** `gamePersistence.js`, `server.js`, `security.js` (`consolidateOutcomesSafely`)  
**Problem:** consolidation-related implementation/coordination exists in several layers. The final runtime installs the exact batched persistence implementation from `gamePersistence.js`, while security/admission coordination and an exported `consolidateOutcomesSafely` helper coexist elsewhere; repository search found no first-party call sites for that exported helper. Multiple apparent owners increase the chance that a future fix changes a non-authoritative path or leaves duplicate behavior drifting.  
**Improvement:** establish one explicit consolidation service/owner and keep security/admission as narrowly defined coordination hooks. Before removing the apparently unused helper, verify it is not an external API and preserve current retry, writer-exclusion, exact-total and cache-invalidation tests.  
**Why deferred:** ownership consolidation touches startup patch order, persistence and concurrency safety and needs focused runtime/test validation.

## Entry format

### QUAL-XXX — Short title
**Location:** `path` / symbol  
**Problem:** Concrete readability, complexity, ownership, coupling, or testability cost.  
**Improvement:** Bounded proposed direction.  
**Why deferred:** Why doing it inside the discovering task would be unsafe or disproportionate.

Remove entries when resolved, disproved or no longer worthwhile. Git history preserves the old state.