# AGENTS.md

Mandatory engineering rules for every coding agent and development task in this repository.

## Minimal bootstrap

1. Follow the user's explicit request and branch target.
2. This root `AGENTS.md` is the only unconditional repository read. After this file, no other repository document is unconditional.
3. For a focused task, go directly to the named/current source, asset, configuration, or focused test when the affected area is obvious. Use the direct routes below only as hints; do not preload route documents merely because they exist.
4. Read `docs/engineering/CONTEXT_INDEX.md` only when the subsystem is unclear, the task crosses subsystem boundaries, the initial exact/direct lookup misses, or the task is a broad audit/architecture review.
5. `PROJECT_PRIMER.md` is opt-in: read it only for explicit cold-start onboarding, broad re-orientation, or when the user asks for it.

### Direct routes

- RL / ML-Agents / training: exact `RlOneVsOne*` symbols, `Training/`, focused RL tests; use `docs/engineering/context/RL.md` when contract/background routing is needed. Unified/continual-training design starts at `Training/bees_continual_learning_rl_implementation.md`.
- Tests / release gate: affected test and source first; use `docs/TESTING.md` only when runner/category/gate mechanics matter.
- Runtime / identity / persistence / pooling: exact `Level`, `GameState`, `ConfigData`, `Ships`, `DataFile` symbols; use `docs/engineering/context/RUNTIME.md` when needed.
- Pathfinding / movement / performance: exact `Pathfinder*` / movement / obstacle symbols and focused tests; use `docs/engineering/context/PATHFINDING.md` when needed.
- Combat / targeting / visibility: exact `Ship`, `Weapon`, `RangeCollider`, damage/command symbols; use `docs/engineering/context/COMBAT.md` when needed.
- Socket / server / reconnect / WebGL: exact socket/request/bridge symbols; use `docs/engineering/context/NETWORKING.md` when needed. Inspect BeesServer too before changing a wire, persistence, reconnect, learning-key, or shared identity contract.
- UI / Squad Maker / viewport: exact guard/controller/prefab/scene first; use `docs/engineering/context/UI.md` when needed.
- Campaign / maps / prefabs: exact mission/asset first; use `docs/engineering/context/CAMPAIGN_ASSETS.md` when needed.
- Replay / engineering workflow / repository learning: exact implementation/test first; use `docs/engineering/context/ENGINEERING.md` when needed.

### Retrieval stop rule

Once you can name the affected contract, current symbols/assets, important caller/callee or owner boundary, and evidence needed to validate the change, **stop loading context**. Expand only for a concrete unresolved dependency, contradiction, retrieval miss, or failed hypothesis. Do not preload broad architecture, memory, regression history, or generic skills for a focused task.

Read `PROJECT_CONSTITUTION.md` only when a task can change gameplay/product meaning, persistence/network contracts, lifecycle/ownership semantics, evidence/validation rules, required quality, or another project-definition boundary. Read relevant `SYSTEM_MAP.md` / `INVARIANTS.md` sections only for high-risk or cross-boundary work, or when a focused route identifies a concrete need.

Maintained documentation is navigation, not unquestioned truth. Revalidate material facts against current code/assets/configuration/tests when correctness depends on them.

## Branch discipline

Respect an explicitly named branch. Otherwise, do not make ordinary development changes directly on `main`; create a descriptive task branch from the latest appropriate base. Specialist skills may impose stricter rules.

## Change safety and validation

Before a behavior-affecting edit, trace the affected path far enough to understand the enduring contract and relevant callers/callees, ownership/lifecycle, serialized assets, persistence/network boundaries, UI/frame/physics behavior, and performance implications. This is not permission for an unrelated repository-wide scan.

For every behavior-affecting change, classify affected tests as **still valid**, **update required**, **obsolete and replaced**, or **missing**. Never delete, skip, weaken, loosen, or rewrite a test merely to make a change pass. Add focused automated protection for a reproducible regression whenever practical; otherwise record the strongest repeatable protection in `docs/engineering/REGRESSIONS.md`.

When execution is available, widen evidence proportionally:

`focused reproducer -> affected subsystem/category -> broader correctness suite -> full local release gate -> representative PlayMode/play/system validation when risk warrants it`

Never claim old XML/logs validate changed source.

## GitHub Actions prohibition

**Do not use GitHub Actions for development, testing, patch application, builds, qualification, or verification. Do not trigger, dispatch, rerun, wait for, inspect, or cite GitHub Actions workflow runs as task evidence.** Required validation must run locally or in an explicitly user-controlled non-Actions environment.

## Repository learning

Use `.agents/skills/search-index/SKILL.md`, `.agents/skills/repo-learning/SKILL.md`, `.agents/skills/continuous-learning/SKILL.md`, `.agents/skills/code-quality/SKILL.md`, and specialist skills only when their procedures are actually needed.

Persist only verified knowledge that prevents a likely repeated mistake or materially reduces future retrieval. Prefer fixing a direct route, subsystem route, or owner statement over adding another mandatory read. New durable guidance should have positive context ROI: it should remove more future searching/reading than it adds. A task may legitimately produce no documentation change.

## Completion gate

Do not call a behavior-affecting task complete until applicable items are true: intent/root cause is understood; important indirect dependencies were checked; affected tests were classified and stale/missing coverage handled; validation reached the strongest practical level; no safety/test contract was weakened; touched code received proportionate quality review; durable retrieval misses were reconciled when worthwhile; and remaining uncertainty or unexecuted validation is reported.
