# Debt found mid-task goes in a debt ledger

**Rule:** When you spot tech debt you are not going to fix in the current task, record it in a ledger so `/debt-sweep` picks it up — don't let it evaporate in a chat transcript, a run file, or a PR comment, and don't derail the current task to chase it.

**Exception:** pre-existing hand-maintained/derived counts in docs are never ledgered — the count in place is its own complete debt record ([`no-derived-aggregates-in-docs`](no-derived-aggregates-in-docs.md)).

**Which ledger — by where the fix lives:**

| Debt | Goes to |
|---|---|
| One-off whose fix is inside a single `src/Sections/Humans.<X>/` — **any** section, not only the one you are working in | that section's [`src/Sections/Humans.<X>/Docs/debt.yml`](../../src/Sections) — create it if absent |
| One-off spanning sections, or in `Humans.Base` / `Humans.Web` / `tests/` / infrastructure | `inbox:` in [`docs/architecture/debt-ledger.yml`](../../docs/architecture/debt-ledger.yml) |
| Recurring class (a pattern with multiple sites, usually analyzer- or baseline-backed) | `themes:` in the central ledger |

A section's own test project (`tests/Humans.<X>.Tests`) is section-owned: its gaps go in that section's `debt.yml`. The `tests/` row above means the shared test projects (`Humans.Testing`, `Humans.Web.Tests`, `Humans.Integration.Tests`).

Section files keep the central ledger readable and put the debt where the next reader of that section will meet it. The central ledger stays the home of rotation state — `themes:` is global by construction, and `/debt-sweep` pools every section file into the same inbox at pick time, so routing changes where an item is written, never whether it is served.

**Entry shape** (identical in both places):

```yaml
version: 1        # section files only; the central ledger declares it once at the top
inbox:
  - added: <YYYY-MM-DD>
    what: "One line naming the file/symbol, what is wrong, the governing rule if known, and how it was found."
    review: light | panel
```

`review: light` only when the fix is rule-prescribed and the verifier is mechanical; otherwise `panel`. Central `themes:` entries additionally carry `id`, `title`, `detect`, `last_swept: never` and `remaining` — rotation serves `never` entries next automatically.

- Ledger-only changes follow [`no-direct-to-main`](no-direct-to-main.md): bundle with the discovery PR, or commit standalone direct to `origin/main`.
- **Write another section's ledger when the debt is theirs — that is the point.** Debt belongs where the next reader of that section will meet it, not in a central pile keyed by who happened to find it. Section ledgers have no single writer and need none: appending to a YAML list rarely collides, and a collision is one hand-resolved hunk. Don't add locking, ownership checks, or a routing detour to avoid it.

**Why:** The sweep's rotation can only be fair over debt it knows about; a ledger entry costs three lines and survives the session.

**How to apply:** Before ending any task where you noticed debt out of scope, ask: is it in a ledger, an existing theme, or a GitHub issue? If none, add the line — to the owning section's file when one section owns the fix, to the central ledger otherwise.
