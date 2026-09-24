# BeesServer Permanent Regression Protections

This is the maintained long-term ledger for regressions whose root cause/protection should survive after the active bug is fixed.

`BUG_LEDGER.md` remains the current unresolved static work queue. `docs/TEST_DEFECTS.md` is the existing historical qualification-defect record and should not be duplicated wholesale here. Add new reusable regression lessons here as they are fixed; migrate older history only when deliberately reconciling it against current code/tests.

## Entry requirements

Each permanent regression entry must contain a verified root cause and a durable protection. Prefer an automated regression test that would fail if the defect returned. If automation is impractical, state why and record the strongest repeatable live/manual/system protection.

Use this format:

### REG-001 — Short regression name
**Area:** subsystem / protocol / persistence / operations  
**Symptom:** What externally incorrect behavior occurred.  
**Root cause:** The verified mechanism that caused it.  
**Permanent protection:** Test(s), invariant(s), structural changes, guards, or operational checks that prevent recurrence.  
**Verification:** Exact validation that demonstrates the protection on the fixing change, or `unexecuted` with reason.  
**Related knowledge:** Optional links to `docs/DEVELOPMENT_MEMORY.md`, `docs/DATABASE_MODEL.md`, or focused invariant documentation.

## Maintenance rules

- This is not a chronological changelog; record only lessons worth carrying forward.
- Do not add speculative root causes.
- When a protection becomes obsolete, replace it with the current protection instead of leaving contradictory entries.
- If a regression proves a general engineering rule, promote that rule to `docs/engineering/INVARIANTS.md` as well.
- If a permanent test is removed or rewritten, review any ledger entries that cite it so the repository does not claim protection it no longer has.

No new entries are backfilled by the guardrail-bootstrap change itself; existing historical defects remain documented in `docs/TEST_DEFECTS.md` until individually reconciled.