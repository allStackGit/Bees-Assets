---
name: code-quality
description: Proportionate touched-code quality gate for BeesServer. Review modified code and only the immediate interfaces needed to judge it; do not expand a narrow change into an unrelated repository audit.
---

# Code Quality

Use this skill when a task changes code-bearing implementation, tests, tooling, schema, migration, or configuration. Pure documentation/agent-skill changes do not require a production-code quality scan unless they directly change a code-quality guardrail.

Apply the gate to every touched code-bearing file and the smallest set of immediate interfaces/ownership assumptions needed to judge it.

## Review dimensions

Check as applicable for:

- correctness, edge cases, failure propagation and invariant preservation;
- clear request/data/connection/transaction ownership and cleanup;
- current modular runtime versus legacy/VM behavior and cross-realm assumptions;
- authentication/trust boundaries, exactly-once request ownership and concurrency ordering;
- exact 64-bit identity/accounting and SQL/JSON conversion safety;
- reservation commit/discard/retry/reconnect semantics and persistence acknowledgement ordering;
- unnecessary complexity, duplication and hidden temporal dependencies;
- names/types/APIs that make unsafe use easy or obscure intent;
- comments that contradict code or obscure the real contract;
- focused testability of failure/concurrency/database behavior;
- avoidable event-loop blocking, O(n²) queue work, DB round trips, serialization/copies, unbounded buffers/caches/retries and filesystem backpressure hazards;
- schema/migration safety, production/test isolation and Unity protocol compatibility;
- dead or misleading compatibility paths exposed by the change.

Do not inspect unrelated modules merely to search for generic cleanup opportunities. Expand only when an immediate dependency is needed to establish correctness or the touched code exposes a concrete wider defect.

## Action rule

Fix a quality problem immediately when it is clearly correct, local, low-risk, proportionate, and behavior-preserving unless behavior change is the task. Add/update tests when the improvement changes a protected contract.

Do not turn a narrow fix into repository-wide cleanup. Route broader validated debt to `QUALITY_LEDGER.md`, actual defects to the bug workflow/ledger, and plausible performance opportunities to `PERFORMANCE_LEDGER.md`.

Do not log cosmetic preferences, formatting churn or speculative refactors.

## Completion

Re-read the final diff as a future maintainer. Changed code should be no harder to understand and should not introduce avoidable defect/performance risk. If a misunderstanding recurs, feed it into the routing/continuous-learning system instead of expanding this quality gate's mandatory scope.