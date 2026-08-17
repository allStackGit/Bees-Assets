---
name: continuous-learning
description: Closed-loop Bees learning that improves routing and reduces rediscovery. Verify candidate lessons, promote only durable high-ROI knowledge, and prefer compression or route repair over documentation growth.
---

# Continuous Learning

The goal is measurable reduction in future rediscovery and repeated mistakes. Documentation growth is not success by itself.

## Candidate triggers

Maintain a small working set only when:

- an important relationship, ownership/lifecycle boundary, serialized contract, or procedure was expensive to rediscover;
- current code/assets/tests disprove maintained guidance;
- the user corrects or repeats a lasting engineering fact;
- a reusable approach fails for a reason future agents are likely to repeat;
- a regression exposes a general rule or missing protection;
- the context index fails and broader searching was required;
- the same workflow friction recurs despite existing guidance.

Transient task facts, cheap lookups, speculative hypotheses, command history, and details already easy to reach are not durable lessons.

## Verify and score context ROI

Before promotion, verify the lesson from current source/assets/configuration/tests. Then ask whether keeping it will reduce future work more than it increases mandatory or likely reading.

A candidate should normally be rejected or compressed unless it does at least one of these:

- replaces a recurring broad search with a precise route;
- prevents a likely repeated defect or invalid assumption;
- captures an expensive-to-reconstruct cross-system relationship;
- corrects stale guidance that would otherwise send agents down the wrong path.

Never solve a local retrieval miss by making another document mandatory for every task.

## End-of-task transaction

Every candidate ends in one state:

- **promote** — verified, durable, positive-context-ROI knowledge is added to the proper owner;
- **refresh** — existing guidance is corrected, compressed, relocated, de-duplicated, or made easier to route to;
- **defer** — potentially useful but not yet verified; record only the evidence still needed in `docs/engineering/LEARNING_STATE.md`;
- **reject** — transient, redundant, too cheap to rediscover, insufficiently verified, or context-negative.

A task with no worthwhile candidate requires no learning-document change.

## Destination priority

Prefer the smallest durable mechanism that solves the future problem:

1. focused automated test/guardrail when recurrence should be executable;
2. compact `CONTEXT_INDEX.md` alias/cross-link when the problem is retrieval;
3. refresh an existing owner statement;
4. add new owner-document detail only when the relationship cannot be represented compactly.

Owners:

- architecture/gameplay/system detail -> `docs/DEVELOPMENT_MEMORY.md`;
- concise ownership/call-path orientation -> `docs/engineering/SYSTEM_MAP.md`;
- stable must-preserve rule -> `docs/engineering/INVARIANTS.md`;
- fixed reusable regression -> test plus `docs/engineering/REGRESSIONS.md`;
- retrieval alias/cross-link -> `docs/engineering/CONTEXT_INDEX.md`;
- unresolved retrieval/candidate state -> `docs/engineering/LEARNING_STATE.md`.

Do not create a second owner for the same fact. Git history stores chronology.

## Retrieval-miss feedback

If the user had to identify an already-known location, the correct subsystem required repeated broad searches, or an existing fact was missed because it was buried, treat that as a learning-system defect. Repair the smallest cause: route, alias, section structure, stale owner text, skill wording, or guardrail.

If a frequently used owner document grows large enough that agents routinely load mostly irrelevant content, split or restructure it behind the index rather than accepting permanent context growth.

## Skill evolution

When workflow guidance itself causes repeated context bloat, delays, or missed facts, change the workflow. New mandatory reads require exceptional justification and should replace a greater recurring cost than they introduce.

## Success criterion

Over repeated tasks, agents should reach the right Bees-specific source and evidence with fewer reads/searches and repeat fewer mistakes. If accumulated learning makes a small task slower, the learning system must be compressed or rerouted.