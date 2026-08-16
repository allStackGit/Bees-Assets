# Quality Ledger

Current unresolved high-value maintainability/clarity debt found by touched-code quality review. This is not a style wishlist or history. Bugs belong in `BUG_LEDGER.md`; performance opportunities belong in the performance workflow/ledger.

### QUAL-001 — Remove tracked IDE workspace state
**Location:** `Scripts/.idea/` and root `.gitignore`  
**Problem:** JetBrains workspace/project-local metadata is tracked inside the runtime source tree while `.gitignore` does not exclude `.idea`. It adds irrelevant search/index context, produces machine-specific churn, and makes a source inventory noisier for humans and agents.  
**Improvement:** Add an appropriate `.idea` ignore rule and remove already tracked workspace metadata after confirming no deliberately shared project configuration is needed.  
**Why deferred:** This seeding pass intentionally does not edit source/repository layout, and tracked-file removal should be a dedicated cleanup change.

### QUAL-002 — Retire or isolate the dormant ML-Agents implementation in `Brain`
**Location:** `Scripts/Entities/Ships/Brain.cs`, `Training/trainer_config.yaml`, bundled ML-Agents material  
**Problem:** `Brain` is currently a `MonoBehaviour`; nearly all former `Agent` callbacks, observations, actions and heuristic code remain as a large commented historical implementation. Current Hive Mind training instead runs through `HiveMindTrainingBootstrap` and the real server learning path. The dormant block is therefore easy for search/repository agents to mistake for active training semantics.  
**Improvement:** Preserve any useful historical design in documentation/Git history, then reduce `Brain` to its active responsibilities or move a deliberately revived ML-Agents experiment behind an explicit separate boundary.  
**Why deferred:** Removing historical code or reviving it changes repository structure and could affect serialized references; it needs a focused validation pass.

### QUAL-003 — Decompose the `SquadMaker` scene controller
**Location:** `Scripts/Scenes/SquadMaker.cs` and existing SquadMaker helpers  
**Problem:** The scene controller owns a very large serialized UI surface while also coordinating persistent fleet/squad editing, build economy, drag/drop, level selection/options, validation/dialogues and scene transition. This raises the context needed for small changes and makes ownership boundaries hard to review safely.  
**Improvement:** Continue extracting cohesive collaborators behind the existing serialized scene facade—for example fleet/squad editing, build/economy, level-option authoring and presentation/drag-drop—while keeping Unity references and persistence behavior stable.  
**Why deferred:** A decomposition is broad and serialized-scene sensitive; doing it during a documentation-only seeding pass would create disproportionate regression risk.

### QUAL-004 — Split schema-like identity maps from the general `Utilities` hub
**Location:** `Scripts/Utilities.cs`  
**Problem:** `Utilities` combines persistent/protocol-sensitive conversion tables (ship letters/types, command/strategy names, sides) with unrelated math, random, file, JSON/XML, UI, scene and transport helpers. The broad dependency surface makes it difficult to tell which changes are compatibility-sensitive and forces agents to load a large unrelated file for simple lookups.  
**Improvement:** Extract stable conversion/identity registries and other cohesive helper families behind compatibility-preserving APIs, leaving `Utilities` as a thinner facade during migration. Add direct tests for mappings that participate in Hive Mind/database protocol identity.  
**Why deferred:** The mappings are widely referenced and some are persistent learning/schema contracts; extraction needs explicit compatibility and full-suite validation.

## Entry format

### QUAL-XXX — Short title
**Location:** `path` / symbol  
**Problem:** Concrete readability, complexity, ownership, coupling, or testability cost.  
**Improvement:** Bounded proposed direction.  
**Why deferred:** Why doing it inside the discovering task would be unsafe or disproportionate.

Remove entries when resolved, disproved or no longer worthwhile. Git history preserves the old state.