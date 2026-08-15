---
name: repo-learning
description: Mandatory repository-learning and change-safety workflow for every development task. Maintain a concise, source-grounded system model; perform impact/test-contract analysis before edits; reconcile stale knowledge and regression protection after edits.
---

# Repository Learning

Use this skill continuously for every coding task in the repository, including ordinary fixes that do not explicitly invoke a named skill. Specialized skills that require this skill inherit all requirements below.

The objective is not to accumulate notes. The objective is to make each completed task leave a more accurate, safer, cheaper-to-reload model of the repository.

## 1. Load the maintained model

Before changing behavior:

1. Read `AGENTS.md` and `PROJECT_CONSTITUTION.md`.
2. Read `docs/DEVELOPMENT_MEMORY.md` and the relevant parts of:
   - `docs/engineering/INVARIANTS.md`;
   - `docs/engineering/SYSTEM_MAP.md`;
   - `docs/engineering/VALIDATION_POLICY.md`;
   - `docs/engineering/REGRESSIONS.md`;
   - `docs/TESTING.md`.
3. Read any focused documents that own the subsystem or contract being changed.
4. Treat all maintained documents as prior models, not unquestioned truth. Revalidate important facts against current code, assets, configuration, and tests when correctness depends on them.

If two maintained documents conflict, do not choose whichever is convenient. Resolve the conflict from current authoritative sources and repair the stale document in the same task when possible.

## 2. Build a pre-change impact model

Before editing, trace enough of the affected system to answer:

- What enduring behavior/contract is being changed or repaired?
- Which callers, callees, state owners, lifecycles, assets, persistence/network boundaries, async paths, and user-visible flows depend on it?
- What looks unusual but is intentional?
- What apparently local change could leak into another Level, pooled lifetime, scene, request, mission, save, or frame?
- Which tests currently claim to protect this behavior and are they still reaching the production path?
- Which validation level would actually expose a regression here?

Classify affected tests using the four states in `AGENTS.md`: still valid, update required, obsolete-and-replaced, or missing.

Do not begin with a narrow patch and postpone impact analysis until after the code happens to pass.

## 3. Learn during investigation and implementation

Record reusable knowledge when it is expensive to rediscover and likely to matter again, including:

- architecture and subsystem responsibilities;
- important entry points, call paths, ownership, lifecycle, and data/state flow;
- stable contracts and invariants;
- non-obvious cross-system assumptions;
- misleading patterns and fragile areas;
- canonical files, assets, configurations, commands, and validation procedures;
- concurrency/ordering/pooling/persistence rules;
- facts that explain why a plausible-looking previous change caused a regression.

Do not record every file read, command run, bug found, or transient hypothesis.

Maintain an explicit distinction between:

- **confirmed current behavior** — verified against present authoritative sources;
- **historical context** — useful explanation that is no longer the current contract;
- **hypothesis/uncertainty** — not yet established and therefore not safe to use as an invariant.

## 4. Maintain, do not append

When durable knowledge changes:

- update the existing document that owns the topic;
- replace or remove stale statements rather than appending contradictions;
- consolidate duplicates;
- keep entries concise and source-grounded;
- prefer `docs/DEVELOPMENT_MEMORY.md` for detailed reusable implementation knowledge;
- prefer `docs/engineering/SYSTEM_MAP.md` for concise ownership/call-path orientation;
- promote stable must-preserve rules into `docs/engineering/INVARIANTS.md`;
- change `PROJECT_CONSTITUTION.md` only for deliberate project-definition changes, not to accommodate current implementation;
- use `docs/engineering/REGRESSIONS.md` for permanent lessons/protection from fixed regressions, not general history.

Dated architectural snapshots may remain as historical references, but the maintained model must clearly prevent stale snapshot claims from outranking current source/tests.

## 5. User reminders become repository knowledge

When the user supplies a lasting engineering fact, warning, or constraint during development:

1. verify it against the repository when verification is possible;
2. determine whether it is a project invariant, architecture fact, testing rule, or temporary task constraint;
3. persist lasting facts in the appropriate maintained repository document during the same task;
4. do not rely on conversational memory as the only future source.

If the user has to repeat the same lasting reminder, treat that as evidence that the repository knowledge system failed and repair the durable source.

## 6. Regression learning

When a real regression is fixed:

- understand the underlying root cause, not only the immediate bad line;
- add a focused automated test that would have failed before the fix whenever practical;
- if automation is impractical, document the concrete manual/system protection and why;
- add or update the permanent entry in `docs/engineering/REGRESSIONS.md` when the regression carries a reusable lesson;
- promote any general ownership/lifecycle/architecture rule into the invariant/system map rather than leaving it only in a bug narrative.

A reproducible regression with no permanent protection is unfinished work unless the user explicitly stops the task before that protection can be added.

## 7. Post-change reconciliation

Before completion:

1. Re-read the changed behavior and affected tests from the perspective of a future maintainer.
2. Confirm the test classification was resolved; do not silently leave a stale test behind.
3. Widen validation according to `AGENTS.md` and `docs/engineering/VALIDATION_POLICY.md` when execution is permitted.
4. Review the maintained knowledge used at task start: did any of it prove stale, incomplete, or misleading?
5. Update/remove stale knowledge and record new durable lessons.
6. Confirm regression protection and permanent-regression records are complete when applicable.
7. Report any validation that could not be executed or any unresolved uncertainty explicitly.

## Efficiency rule

Repository memory should reduce future reading without replacing source verification. The desired trend is that future agents can identify the correct subsystem, invariants, tests, and danger surfaces faster while making fewer assumptions.

More documentation is not automatically better. A small maintained map that is correct is better than a large notebook of stale observations.