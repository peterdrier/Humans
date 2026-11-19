---
name: no-derived-aggregates-in-docs
description: HARD RULE. Never write a doc number derived from data already in that doc — no list counts, `Total` rows, or code-owned-set counts. Any count you find, delete it on sight; never refresh it.
---

**HARD RULE.** Never write a number into a doc that is derived from something else.

Covers:
- Counts of a list on the same page ("**five** sections:", "three things follow").
- `Total` and subtotal rows; summary lines restating a count stated elsewhere.
- A count in doc A of items listed in doc B.
- Count-reconciliation changelog ("was 9, then 8"). Git records that.
- **Counts of a set the code owns** ("there are 43 sections", "25 services implement X"), even with no
  list in the doc. Write "all of them", name them, or give the defining predicate. Peter: *"we don't
  allow counts in docs. remove the number — 'all of them' is sufficient."*

**Why:** a derived number is a hand-maintained copy with nothing keeping it honest; it drifts silently
the moment the source changes, and every later correction has to chase the fact plus its shadows.

**How to apply:**
- Write the list or table once and stop. A summary carries judgment, never arithmetic. Where a roll-up
  earns its keep, generate it from structured data (the `freshness-catalog.yml` / `debt-ledger.yml`
  pattern).
- **The fix is deletion, never a refresh.** A count your change invalidates is removed, not updated.
- **Pre-existing counts: delete on sight**, in the change you are already making. No question to Peter,
  no follow-up. Exception: never edit Peter's own rule files; tell him instead.
- **Never log one** in a ledger, run file or issue; that creates another shadow copy.
- **Sweep the whole diff, not the flagged line**, before pushing a docs change:

  ```bash
  git diff origin/main...HEAD -- '*.md' '*.yml' | grep '^+' | grep -v '^+++' \
    | grep -inE '\b(one|two|three|four|five|six|seven|eight|nine|ten|[0-9]+) [a-z][a-z-]*(s|es)\b'
  ```

  It over-reports (dates, issue refs, thresholds are fine). For each hit ask: is this number derived
  from something else?
