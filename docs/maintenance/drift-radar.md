# Weekly drift radar

The Saturday slot of the [maintenance dispatcher](dispatcher.md) follows this doc to spot **horizontal slop and emerging drift** across the whole codebase before small habits grow into repo-wide messes — and to flag **legacy rules** that are actively getting in agents' way. Anyone can also run it by hand: "follow docs/maintenance/drift-radar.md".

## Posture

- **Read-only toward the repo.** This run does not edit code, add rules, or open a PR. It reports to Peter by opening at most one concise GitHub issue on `peterdrier/Humans` when something trips the bar.
- **Never propose CI checks or build gates** ([`no-new-ci-checks`](../../memory/process/no-new-ci-checks.md)). Name the finding and pick a non-CI lever.
- **Unattended-safe** ([`unattended-runs-never-prompt`](../../memory/process/unattended-runs-never-prompt.md)): run repo scripts by their repo path, stay inside the checkout, never wait on a prompt.

## Steps

1. **Backpressure check.** List open issues on `peterdrier/Humans` with label `drift-radar`:
   ```bash
   gh issue list --repo peterdrier/Humans --state open --label drift-radar --json number,title,url
   ```
   If an earlier `drift-radar` issue is still open, stop: report its link in one line and exit. Nothing is queued.

2. **Stage 1 — Deterministic velocity scan.** Run the telemetry script against `HEAD` vs. 30 days ago:
   ```bash
   node scripts/drift-metrics.mjs --days 30
   ```
   Focus on metrics that **accelerated** (flagged rows) and their top `file:line` outliers — static legacy counts that are flat or shrinking are not radar findings.

3. **Stage 2 — Unknown-unknowns hunk sampling.** Metrics only catch shapes already thought of. Pull stratified recent diffs from the last two weeks:
   ```bash
   node scripts/drift-metrics.mjs --sample-hunks --days 14
   ```
   Read the sampled hunks fresh across services, controllers, views, tests, and `memory/` asking two questions:
   - What repetitive, over-engineered, or subtly-wrong habit is spreading here that no rule or analyzer covers yet?
   - Where are agents writing awkward contortions or boilerplate to satisfy a stale or over-broad rule?

   Every candidate from Stage 2 must be verified repo-wide with a grep or count before admitting it. Discard one-off quirks; only spreading patterns or recurring friction survive.

4. **Filter & rank.** Drop anything that:
   - matches an entry under `## Declined` below;
   - is already an active theme in `docs/architecture/debt-ledger.yml`; or
   - is transient churn from an in-flight migration rather than a durable drift pattern.

   Rank what remains by **rate of spread × reader/maintenance cost** and keep **at most 3 findings**. If nothing passes the bar, open no issue and end with a one-line clean report.

5. **Open one issue on `peterdrier/Humans`.**
   - Title: `Drift radar YYYY-MM-DD: <short headline>`
   - Labels: `drift-radar` (create the label once if missing, or omit if label creation is unavailable) and `section: TBD` (or the owning `section:<name>` if all findings belong to one section, per [`issues-need-section`](../../memory/process/issues-need-section.md)).
   - Body: one short section per finding (max 3), each containing:
     - **What is spreading** (or **which rule is getting in the way**), in two sentences.
     - **Velocity:** the 30-day delta (or repo-wide grep count + recent commit dates).
     - **Evidence:** up to 3 verified `file:line` examples.
     - **Recommended lever** — pick **one**:
       - graduate to an in-repo Roslyn analyzer (`src/Humans.Analyzers/`);
       - seed a theme in `docs/architecture/debt-ledger.yml` for `/debt-sweep`;
       - add, narrow, or **retire** a rule in `memory/` or `docs/architecture/code-review-rules.md`;
       - record under `## Declined` below if intentional.
   - End the issue with a collapsible `<details>` block containing the Stage 1 markdown table.

6. **Report** in at most 5 lines: the issue link and one line per finding (or "No drift findings above threshold" if none).

## Declined

Patterns Peter has explicitly dismissed so future Saturdays do not re-pitch them. Add entries here only at Peter's direction (`<id> — <reason and date>`).

*(none yet)*
