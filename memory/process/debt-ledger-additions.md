# Debt found mid-task goes in a debt ledger

**Rule:** When you spot tech debt you are not going to fix in the current task, record it in a ledger so `/debt-sweep` picks it up — don't let it evaporate in a chat transcript, a run file, or a PR comment, and don't derail the current task to chase it.

**Which ledger — by where the fix lives:**

| Debt | Goes to |
|---|---|
| One-off whose fix is inside the section you are working in | that section's [`src/Sections/Humans.<X>/Docs/debt.yml`](../../src/Sections) — create it if absent |
| One-off in a *different* section, spanning sections, or in `Humans.Base` / `Humans.Web` / `tests/` / infrastructure | `inbox:` in [`docs/architecture/debt-ledger.yml`](../../docs/architecture/debt-ledger.yml) |
| Recurring class (a pattern with multiple sites, usually analyzer- or baseline-backed) | `themes:` in the central ledger |

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
- **Append to a section file only for the section you are working on.** Debt you find in some *other* section goes to the central `inbox:`, whoever finds it — that is what keeps each section file single-writer, and `/debt-sweep` pools both so the item is served either way. `/debt-sweep` itself deletes entries it drains from any ledger; it is one process editing merged state.

**Why:** The sweep's rotation can only be fair over debt it knows about; a ledger entry costs three lines and survives the session.

**How to apply:** Before ending any task where you noticed debt out of scope, ask: is it in a ledger, an existing theme, or a GitHub issue? If none, add the line — to the section's own file when the fix lives in the section you are working in, to the central ledger otherwise.
