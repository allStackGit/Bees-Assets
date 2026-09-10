# AGENTS.md

Mandatory rules for every coding/development task in this repository.

## Bootstrap

1. Follow the user's request and branch target.
2. This root `AGENTS.md` is the only unconditional repository read. After this file, no other repository document is unconditional.
3. Focused task: inspect the exact named/current source, asset, config, or focused test first. Routes are hints, not required reads.
4. Read `docs/engineering/CONTEXT_INDEX.md` only when the subsystem is unclear, boundaries cross, direct lookup misses, or the task is a broad audit/architecture review.
5. `PROJECT_PRIMER.md` is opt-in for explicit cold-start onboarding/re-orientation only.

### Direct routes

- RL/training → `RlOneVsOne*`, `Training/`, focused tests; optional `docs/engineering/context/RL.md`. Unified/continual design → `Training/bees_continual_learning_rl_implementation.md`.
- Tests → affected source/test; `docs/TESTING.md` only for runner/gate mechanics.
- Runtime/persistence/pooling → `Level`, `GameState`, `ConfigData`, `Ships`, `DataFile`; optional `context/RUNTIME.md`.
- Pathfinding/performance → `Pathfinder*`, movement/obstacles; optional `context/PATHFINDING.md`.
- Combat/targeting → `Ship`, `Weapon`, `RangeCollider`; optional `context/COMBAT.md`.
- Network/WebGL → socket/request/bridge code; optional `context/NETWORKING.md`; inspect BeesServer before shared-contract changes.
- UI → exact guard/controller/prefab/scene; optional `context/UI.md`.
- Campaign/assets → exact mission/asset; optional `context/CAMPAIGN_ASSETS.md`.
- Replay/agent workflow → exact implementation/test/skill; optional `context/ENGINEERING.md`.

### Retrieval stop rule

Once the affected contract, current symbols/assets, important dependency/owner boundary, and validation evidence are known, **stop loading context**. Expand only for a concrete unresolved dependency, contradiction, retrieval miss, or failed hypothesis. Do not preload broad architecture, memory, regression history, or generic skills.

Read `PROJECT_CONSTITUTION.md` only for possible gameplay/product meaning, persistence/network contract, lifecycle/ownership, evidence-rule, or project-definition changes. Read relevant `SYSTEM_MAP.md` / `INVARIANTS.md` sections only for high-risk/cross-boundary work or a concrete routed need. Documentation is navigation, not authority; verify material facts against current source/assets/tests.

## Change rules

Respect an explicitly named branch; otherwise do not make ordinary development changes directly on `main`.

Before behavior changes, trace only relevant contracts, dependencies, ownership/lifecycle, serialized/persistence/network boundaries, UI/frame/physics behavior, and performance implications. Do not turn this into an unrelated scan.

Classify affected tests as **still valid**, **update required**, **obsolete and replaced**, or **missing**. Never weaken/delete/skip a test merely to pass. Add focused regression protection when practical; otherwise use `docs/engineering/REGRESSIONS.md`.

Widen evidence proportionally:
`focused reproducer -> affected subsystem/category -> broader correctness suite -> full local release gate -> representative PlayMode/play/system validation when warranted`

Never claim old XML/logs validate changed source. **Do not use GitHub Actions for development, testing, patching, builds, qualification, or verification.**

## Learning and completion

Use `.agents/skills/search-index/SKILL.md`, `.agents/skills/repo-learning/SKILL.md`, `.agents/skills/continuous-learning/SKILL.md`, `.agents/skills/code-quality/SKILL.md`, and specialist skills only when needed. Persist only verified knowledge that prevents repeated mistakes or reduces future retrieval; prefer repairing routes/owner statements over mandatory reads. New durable guidance needs positive context ROI.

For behavior changes, require understood intent/root cause and important dependencies, test classification, strongest practical validation, no weakened safety/test contract, proportionate touched-code review, and disclosure of remaining uncertainty/unexecuted validation.
