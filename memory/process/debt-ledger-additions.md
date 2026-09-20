---
name: Debt found mid-task goes in a debt ledger
description: Tech debt spotted mid-task goes in a ledger (section's `Docs/debt.yml`, or central `debt-ledger.yml`) for `/debt-sweep`; code judged sound is not debt.
---

# Debt found mid-task goes in a debt ledger

**Rule:** When you spot tech debt you are not going to fix in the current task, record it in a ledger so `/debt-sweep` picks it up — don't let it evaporate in a chat transcript, a run file, or a PR comment, and don't derail the current task to chase it.

**Exception:** pre-existing hand-maintained/derived counts in docs are never ledgered — the count in place is its own complete debt record ([`no-derived-aggregates-in-docs`](no-derived-aggregates-in-docs.md)).

**Not debt:** code you examined and judged sound. A ledger entry means "this needs fixing"; recording "looked at it, it's fine" so a later reader knows it was examined turns the ledger into a review transcript nobody can act on. Leave it out — or, if the judgment is worth pinning at the code, pin it at the code.

**Which ledger — by where the fix lives:**

| Debt | Goes to |
|---|---|
| One-off whose fix is inside a single `src/Sections/Humans.<X>/` **or its own test project `tests/Humans.<X>.Tests/`** — **any** section, not only the one you are working in | that section's [`src/Sections/Humans.<X>/Docs/debt.yml`](../../src/Sections) — create it if absent |
| One-off spanning sections, or in `Humans.Base` / `Humans.Web` / shared `tests/` (`Humans.Testing`, `Humans.Integration.Tests`, …) / infrastructure | `inbox:` in [`docs/architecture/debt-ledger.yml`](../../docs/architecture/debt-ledger.yml) |
| Recurring class (a pattern with multiple sites, usually analyzer- or baseline-backed) | `themes:` in the central ledger |

The `tests/` in the second row means the shared test infrastructure — `tests/Humans.Testing`, the architecture-test baselines, the harness. A **section's own test project** (`tests/Humans.<X>.Tests`) is section-owned like the section itself, so a test gap there goes in that section's `Docs/debt.yml`, by the first row. (Codex read the row literally on peterdrier/Humans#1553; the first row's "fix is inside a single section" is what decides it.)

Section files keep the central ledger readable and put the debt where the next reader of that section will meet it. The central ledger stays the home of rotation state — `themes:` is global by construction, and `/debt-sweep` pools every section file into the same inbox at pick time, so routing changes where an item is written, never whether it is served.

**Entry shape** (identical in both places):

```yaml
version: 1        # section files only; the central ledger declares it once at the top
next_id: 12       # the next id number for THIS file — the only source of it
inbox:
  - added: <YYYY-MM-DD>
    id: TEAMS-11
    what: "One line naming the file/symbol, what is wrong, the governing rule if known, and how it was found."
    review: light | panel
    status: needs-detail          # optional; omitted means open
    blocked_on: "What would make it checkable."   # required with status: needs-detail
    root: CENTRAL-57              # optional; this row is one symptom of that row's cause
```

`review: light` only when the fix is rule-prescribed and the verifier is mechanical; otherwise `panel`. Central `themes:` entries additionally carry `id`, `title`, `detect`, `last_swept: never` and `remaining` — rotation serves `never` entries next automatically.

**Ids.** `<PREFIX>-<n>`: the file's short uppercase prefix (`GOV`, `USERS`, `CENTRAL`, …) plus the number `next_id` is sitting on; bump `next_id` in the same edit. **Numbers are never recycled** — `next_id` only increases, including past deletions, so a closed id stays closed forever and a PR that cites one always means the same item.

**Closing an item** = delete its row and cite its id in the PR. Nothing is marked done in place.

**One item = one defect one PR can close and one check can verify.** If closing a row takes two independent fixes, it is two rows — split it, giving each its own id. Conversely, when many rows are symptoms of one cause, keep the rows and point each at a parent row with `root:`; the parent states the cause so the ladder works it once instead of N times.

**`status:`** — `open` (the default, omit it), `needs-detail` (the claim cannot be proved or disproved as written; `blocked_on:` says in one line what would make it checkable — a human sharpens it, nobody silently drops it), `partial` (part of the row is already fixed; narrow `what:` to the part that is still broken and say what was verified fixed).

- Ledger-only changes follow [`no-direct-to-main`](no-direct-to-main.md): bundle with the discovery PR, or commit standalone direct to `origin/main`.
- **Write another section's ledger when the debt is theirs — that is the point.** Debt belongs where the next reader of that section will meet it, not in a central pile keyed by who happened to find it. Section ledgers have no single writer and need none: appending to a YAML list rarely collides, and a collision is one hand-resolved hunk. Don't add locking, ownership checks, or a routing detour to avoid it.

**Why:** The sweep's rotation can only be fair over debt it knows about; a ledger entry costs three lines and survives the session.

**How to apply:** Before ending any task where you noticed debt out of scope, ask: is it in a ledger, an existing theme, or a GitHub issue? If none, add the line — to the owning section's file when one section owns the fix, to the central ledger otherwise.
