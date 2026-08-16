---
name: continuous-learning
description: Closed-loop repository learning for every substantive Bees task. Extract candidate lessons, verify and classify them, promote only durable knowledge, record unresolved retrieval/learning failures, curate stale knowledge, and improve agent procedures when mistakes repeat.
---

# Continuous Learning

The goal is measurable reduction in future rediscovery and repeated mistakes, not accumulation of notes.

## Candidate triggers

Maintain a small working set of candidate lessons when:

- an important relationship, call path, lifecycle/ownership boundary or serialized contract was expensive to rediscover;
- current code/assets/tests disprove or materially refine maintained documentation;
- the user corrects or repeats a lasting engineering fact;
- a plausible approach fails for a reusable reason;
- a regression exposes a general rule or missing protection;
- a debugging, validation or optimization procedure proves reliably useful;
- the search/index path fails and broad scanning was required;
- the same agent mistake/friction recurs despite existing guidance;
- touched-code review exposes recurring maintainability debt.

Transient task facts, cheap lookups, speculative hypotheses and command history are not durable lessons.

## Verify before promotion

For each candidate, identify supporting current source, serialized asset/configuration and/or test evidence. Do not promote a hypothesis into an invariant. For campaign/UI/network behavior, verify the relevant multi-source contract rather than trusting a single stale file.

## End-of-task learning transaction

Every candidate ends in one state:

- **promote** — verified new durable knowledge is written to its owner document/test/index;
- **refresh** — existing knowledge is corrected, compressed, relocated or de-duplicated;
- **defer** — potentially important but not sufficiently verified; add a concise item to `docs/engineering/LEARNING_STATE.md` with evidence still needed;
- **reject** — transient, disproved, redundant, too cheap to rediscover or unsafe to generalize; do not persist it.

A task with no worthwhile candidates requires no documentation change.

## Destination rules

- architecture, gameplay/system data flow, important asset relationships -> `docs/DEVELOPMENT_MEMORY.md`;
- concise ownership/navigation -> `docs/engineering/SYSTEM_MAP.md`;
- stable must-preserve rule -> `docs/engineering/INVARIANTS.md`;
- fixed regression -> focused test plus `docs/engineering/REGRESSIONS.md` when reusable;
- retrieval alias/cross-link -> `docs/engineering/CONTEXT_INDEX.md`;
- unresolved candidate/retrieval miss/repeated agent failure -> `docs/engineering/LEARNING_STATE.md`;
- maintainability debt outside safe task scope -> `QUALITY_LEDGER.md`; actual defects/performance opportunities use their dedicated workflows/ledgers.

Do not create a second owner for the same fact.

## Retrieval-miss feedback

Treat these as index/learning failures: the user had to point to an already-known location; the correct subsystem required repeated repository-wide searches; an existing durable fact was missed because its retrieval path was unclear; or an agent repeated a documented mistake.

After resolving the task, repair the smallest cause: add/adjust a context-index alias, restructure an overgrown memory section, strengthen a skill/guardrail, or correct stale knowledge. Do not merely add another copy of the fact.

## Skill evolution and curation

When the same class of agent mistake or workflow friction recurs despite existing guidance, inspect why the guidance failed. If a general procedural change is justified, update the relevant skill and guardrail test. Skill changes remain Git-versioned/reviewable and may not weaken gameplay, tests, safety or evidence requirements.

Curate when `LEARNING_STATE.md` accumulates related items, a retrieval miss repeats, an owner document develops duplicate/stale sections, or the context index becomes noisy. Prefer deletion, consolidation and better links over growth. Git history holds chronology.

## Success criterion

Over repeated tasks, agents should need fewer broad reads/searches, recover important cross-system connections earlier, repeat fewer mistakes and make more Bees-specific suggestions. Current source/assets/tests remain mandatory verification for changed facts.