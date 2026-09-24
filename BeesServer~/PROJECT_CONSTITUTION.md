# BeesServer Project Constitution

This file defines stable backend requirements. Ordinary bug fixes, optimizations, test repairs, and refactors may not weaken these rules merely to obtain a passing test or faster run. Deliberate changes to these rules are project-definition changes.

## Purpose

BeesServer provides the durable backend for Bees: authenticated client admission, user/settings persistence, Hive Mind strategy selection and outcome learning, reconnectable game/outcome ownership, schema evolution, and operational services required by the Unity client.

Correctness is measured across the full Unity -> WebSocket -> server -> persistence -> response contract, not by isolated helper behavior alone.

## Data integrity

- A success response that implies durable persistence must not be sent before the required durable commit succeeds.
- Database transactions must be real transactions: all transactional statements use the same borrowed connection and failures roll back/fail closed.
- Temporary outcome ownership is resolved by valid commit or explicit discard. Disconnect, Game retirement, wall-clock age, or a failed sibling write must not silently lose a still-valid outcome.
- Exact identifiers and accumulated values must remain exact across JavaScript/MySQL boundaries. Values outside JavaScript's safe integer range are not converted to `Number`.
- Failed reads that are semantically errors must not be reinterpreted as valid empty state.

## Protocol and concurrency integrity

- The Unity-facing request/response contract, persistent strategy IDs, request hashes, matchup identities, and ownership semantics are compatibility surfaces.
- One WebSocket may host multiple Levels. Shared connection/Game state must not let concurrent requests overwrite request-local identity or context.
- Dependency ordering may serialize state transitions that require it, but independent requests should not be globally serialized merely for convenience.
- Every accepted/pending request ownership key must be released exactly once on terminal or abandoned paths.
- Authentication, reconnect ownership, and user identity must fail closed rather than permitting cross-user takeover or unauthenticated mutation.

## Production safety

- Automated tests and qualification use `bees_test`, never production database `ram`.
- Production schema/data mutation is an explicit operational act. Development workflows must not silently enable production migrations or repoint test tooling at production.
- Test helpers, injected dependencies, and diagnostic modes may simplify infrastructure but must not be presented as production-path evidence when they bypass the contract being validated.

## Learning-system integrity

Hive Mind history/outcome persistence is part of the gameplay learning system. Optimizations and cleanup must preserve strategy identity, outcome attribution, exact uses/TSV accounting, cache invalidation, consolidation semantics, and the evidence used for future strategy selection.

A faster implementation is invalid if it drops outcomes, changes attribution, rounds exact values, reorders ownership transitions unsafely, or weakens persistence/retry semantics.

## Operational resilience

The modular runtime must preserve required startup/background lifecycle behavior while containing failures. Cache persistence, recovery/export, consolidation, authentication, queue handling, and shutdown paths must avoid unbounded memory growth, event-loop pathologies, silent data loss, and process termination from unhandled asynchronous errors.

## Change rule

When implementation and this constitution disagree, implementation is considered defective unless the project owner explicitly changes the project definition. Tests should move toward these requirements; these requirements should not be weakened merely because an existing implementation or test assumes otherwise.