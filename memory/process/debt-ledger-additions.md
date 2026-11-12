---
name: debt-ledger-additions
description: Tech debt spotted mid-task goes in a ledger (section's `Docs/debt.yml`, or central `debt-ledger.yml`) for `/debt-sweep`; code judged sound is not debt.
---

Debt you spot and won't fix in the current task goes in a ledger so `/debt-sweep` picks it up. Don't
leave it in a chat transcript, run file or PR comment, and don't derail the task to chase it. Debt runs
themselves add rows sparingly ([`debt-ledger-must-shrink`](debt-ledger-must-shrink.md)).

**Not ledgered:** derived counts in docs, which are deleted on sight
([`no-derived-aggregates-in-docs`](no-derived-aggregates-in-docs.md)); and code you examined and judged
sound. A row means "this needs fixing".

**Which ledger — by where the fix lives:**

| Debt | Goes to |
|---|---|
| One-off inside a single `src/Sections/Humans.<X>/` or its own `tests/Humans.<X>.Tests/` — any section, not only yours | that section's `Docs/debt.yml` (create it if absent) |
| One-off spanning sections, or in Base / Web / shared test infrastructure | `inbox:` in [`docs/architecture/debt-ledger.yml`](../../docs/architecture/debt-ledger.yml) |
| Recurring class with multiple sites (usually analyzer- or baseline-backed) | `themes:` in the central ledger |

Writing another section's ledger is the point: debt sits where that section's next reader meets it. No
locking or ownership checks. The sweep pools every section file at pick time.

**Entry shape** (same in both places):

```yaml
version: 1        # section files only
next_id: 12       # next id number for THIS file
inbox:
  - added: <YYYY-MM-DD>
    id: TEAMS-11
    what: "One line: file/symbol, what is wrong, the governing rule if known, how it was found."
    review: light | panel
    status: needs-detail          # optional; omitted means open
    blocked_on: "What would make it checkable."   # required with needs-detail
    root: CENTRAL-57              # optional; this row is a symptom of that row's cause
```

- `review: light` only when the fix is rule-prescribed and the verifier is mechanical. Central `themes:`
  entries also carry `id`, `title`, `detect`, `last_swept: never` and `remaining`.
- **Ids** are `<PREFIX>-<n>` from the file's `next_id`; bump it in the same edit. Numbers are never
  recycled.
- **Closing** = delete the row and cite its id in the PR. Nothing is marked done in place.
- **One row = one defect one PR can close and one check can verify.** Two independent fixes are two rows;
  many symptoms of one cause keep their rows and point at a parent with `root:`.
- **`status:`** `needs-detail` (cannot be proved or disproved as written; `blocked_on:` says what would
  make it checkable) or `partial` (narrow `what:` to the part still broken).
- Ledger-only changes follow [`no-direct-to-main`](no-direct-to-main.md).

**Why:** the sweep's rotation is only fair over debt it knows about, and a row survives the session.
