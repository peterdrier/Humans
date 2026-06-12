# Debt found mid-task goes in the debt ledger

**Rule:** When you spot tech debt you are not going to fix in the current task, record it in [`docs/architecture/debt-ledger.yml`](../../docs/architecture/debt-ledger.yml) so `/debt-sweep` picks it up — don't let it evaporate in a chat transcript, and don't derail the current task to chase it.

- **Recurring class** (a pattern with multiple sites, usually analyzer- or baseline-backed) → append a `themes:` entry with `last_swept: never` and an honest `review:` tier (`light` only when the fix is rule-prescribed and the verifier is mechanical; otherwise `panel`). Rotation serves `never` entries next automatically.
- **One-off item** → append to `inbox:` with `added: <YYYY-MM-DD>`, a one-line `what:` (include the file/symbol and the governing rule if known), and `review:`.
- Ledger-only changes follow [`no-direct-to-main`](no-direct-to-main.md): bundle with the discovery PR, or commit standalone direct to `origin/main`.

**Why:** The sweep's rotation can only be fair over debt it knows about; a ledger entry costs three lines and survives the session.

**How to apply:** Before ending any task where you noticed debt out of scope, ask: is it in the ledger, an existing theme, or a GitHub issue? If none, add the ledger line.
