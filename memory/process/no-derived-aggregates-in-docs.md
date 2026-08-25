# No derived aggregates in docs

**HARD RULE.** Never write a number into documentation that is derived from data already
present in that documentation. No `count = 3` under a list of three items. No `Total` row
summing a table. No "G1: 4 gaps" verdict line under a gap list of four. If a reader wants the
count, they can do it mentally.

This covers:

- Counts of a list that sits on the same page ("**five** sections:", "the exception list stays
  at **three**", "Three things follow").
- `Total` / subtotal rows in a table whose body already carries the values.
- Summary sections that restate a count, cycle tally, or edge total stated elsewhere in the
  same document.
- A count in doc A of items enumerated in doc B (a tracker cell holding a gap count owned by a
  scorecard).
- Count-reconciliation changelog — "was 9, then briefly 8", "total moves 6 → 7". Git history
  records how a number changed; the doc should not.
- **Counts of a set the code owns, with no list in the doc at all** — "there are **43**
  sections", "**30** own tables", "25 section services implement `IUserDataContributor`",
  "24 section projects reference `Humans.Gdpr.Contracts`". Same defect one step out: the
  generator is the compiler rather than an adjacent list, so it drifts on the next section,
  contributor or table and nothing fails. Write "all of them", "they are: …", or a defining
  predicate ("those with a paired `.Contracts` project") instead. Peter, 2026-08-25: "we
  don't allow counts in docs. remove the number — 'all of them' is sufficient."

**Why:** derived numbers are hand-maintained copies with no generator keeping them honest, so
they drift the moment the underlying list changes — and they drift silently, because nothing
fails. The Q3 G0 audit docs (nobodies-collective/Humans#1153) accumulated 115 review findings
across 11 rounds, and the large majority were a derived copy disagreeing with its own source
list rather than any error in the underlying audit. Every correction had to be applied to the
fact plus its three or four shadows, and the shadow that got missed became the next round's
finding.

**How to apply:** write the list, the table, or the predicate rows once and stop. Where a
roll-up genuinely earns its keep, generate it from structured data rather than typing it —
the [`freshness-catalog.yml`](../../docs/architecture/freshness-catalog.yml) and
[`debt-ledger.yml`](../../docs/architecture/debt-ledger.yml) pattern. A summary section should
carry *judgment* ("the Users/Onboarding conflation is the headline structural problem"), never
arithmetic. Qualitative characterizations are fine and welcome; it is specifically the numbers
that must not be duplicated.

Related: [[reuse-first-change-discipline]], [[rules-maintenance]].
