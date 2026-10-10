# Nightly Debt Ladder

A standing, top-down list of **work types**, not a nightly checklist. Open
ledger rows come first: the rungs order and explain the work on them, and
their Finds commands look for new debt only once no ledger row is actionable.
The ledger must end every run smaller than it started (`daily-debt.md`). Make a
brief rung-1 hygiene pass, then work substantive rungs top-down. After every
fix, continue with another safe candidate until the work window elapses;
then finish the current task. One run produces one PR containing all fixes.
No item count or drained rung is a stopping condition before that deadline.

Guardrails, working loop, and the timed-goal/commit protocol live in
`daily-debt.md`. This file is only the priority order and the per-rung
mechanics: what it is, how to find tonight's candidates, how to tell the rung
is drained, and the done-check.

**Every rung obeys `daily-debt.md`'s guardrails without exception** — and
`memory/process/debt-sweep-standing-policy.md` (D1–D13): rule-prescribed
contract changes are pre-approved and listed in the PR (D1), `internal` types
are never public surface (D2), a new interface/service/endpoint still goes to
Peter in D3 format, no category-bin skips (D4); one coherent fix or
same-correction batch per commit (possibly across sections), six cultures,
migrations only via `dotnet ef migrations add`, never touch `NoDestructiveMigrationOps.baseline.txt` or a `[DontFix]` class.

Every ledger row carries a permanent `id:` (`memory/process/debt-ledger-additions.md`). Identify
an item by its id everywhere — in the PR body, in commit messages, in this file's seed lists — and
by `file:line` only for a baseline/props entry, which has no ledger row. The PR body **must** list
the ids it closed.

**Seed lists below are dated 2026-09-20** (from a full-corpus audit; Rungs
3, 5 and 6 corrected 2026-10-06). Seeds and counts are not facts (D10):
re-derive them from each rung's Finds/detector every night, and fix any stale
text in this file in the same PR. Verify
each is still true before touching it — code moves fast and a seed item that's
already fixed is rung-1 work (close it), not rung-N work. Once a rung's seed
list is exhausted, work the open ledger rows that rung covers, then its
**Finds** command for anything not yet in a ledger.

---

## Rung 1 — Ledger hygiene

**What:** Close `inbox:`/`themes:` rows in `docs/architecture/debt-ledger.yml`
and `src/Sections/*/Docs/debt.yml` whose described defect the code has since
fixed. Pure deletion/correction of ledger text — no `src/` change, so no build
or test gate. **Recurring — never fully drains**, because new rows go stale
over time too. Do this first every night, cheaply, before picking a "real"
rung; it costs little and keeps every other rung's Finds command honest.
**This is bookkeeping, not a substantive fix. Never stop or publish a PR
after only this rung; continue to production code or executable tooling.**

**Finds:** for each candidate row, pull the code symbol(s)/file the `what:`
text names and grep for them; if the cited method, file, or text no longer
exists (or the surrounding code now contradicts the claim), the row is stale.
```
grep -rn "<symbol or path named in what:>" src/ tests/ docs/
```
Prioritize rows you have least confidence are still live (oldest `added:`
dates, or ones whose `what:` describes a "stale comment"/"doc says X" pattern
— those flip fastest as code moves).

**Seed (verify each, then delete the row / fix the note):**
- `src/Sections/Humans.Teams/Docs/debt.yml` — the `IShiftManagementService`
  data-access.md row: already reads `IShiftManagementServiceRead`, both sites.
- `src/Sections/Humans.Users/Docs/debt.yml` — the `AccountDeletionService`
  "false thread-safety rationale" row: `grep -i thread` now returns nothing.
- `src/Sections/Humans.Email/Docs/debt.yml` — the `ISystemSettingsService`
  data-access.md row: doc already says `ISettingsService` throughout.
- `src/Sections/Humans.Calendar/Docs/debt.yml` — the `ICalFeedService.cs`
  stale thread-safety comment row: comment already rewritten.
- `src/Sections/Humans.Search/Docs/debt.yml` — the `IShiftManagementService`
  doc/csproj row: `SearchService.cs` already injects the `Read` leaf.
- `docs/architecture/debt-ledger.yml` `inbox:` — close: the
  `burnername-is-the-display-name.md` stale-atom row (already rewritten
  2026-09-18); the `dietary-medical-nudge.md:73` dead-link row (already
  points at `Users.md`); the `TeamConfiguration RequiresApproval` sentinel
  row (`.HasSentinel(true)` already present); the `DashboardService reads
  EventSettings via wrong interface` row (`GetMemberDashboardAsync` no
  longer exists — dashboard was rearchitected into per-section
  contributors); the pre-G5 dead freshness-trigger-path row (no
  `Humans.Application/Domain/Infrastructure/UI` paths remain under
  `docs/guide/`); the `UsersAdminController.Audience` direct-DbContext row
  (now goes through `IUsersAudienceService`); the `FeedbackService
  IFormFile/FileStream` row (already on `IFileStorage`); the
  `AuditLogRepository/UserRepository.PurgeAsync/AccountMergeRepository`
  cross-domain row (superseded by the `IUserDataContributor` fan-out); the
  `Finance/Creditors/Resync` dead-route-in-docs row (already removed from
  both docs).
- `docs/architecture/debt-ledger.yml` `themes:` — correct, don't delete.
  Re-run each theme's own detector and write what it returns into that
  theme's `remaining:`; do not carry a number in here, and do not trust one
  written here before (`memory/process/no-derived-aggregates-in-docs.md` —
  a count restated in prose is a shadow copy that goes stale silently, and
  this file is prose). Three are known to be overstated:
  `obsolete-user-displayname`, `baseline-display-sort`, and
  `cs0618-pragma-nav-reads` — the last badly, since `Humans.Infrastructure`
  no longer exists and its share of that count went with it. Theme
  bookkeeping (D11): add a missing `parked:` key where a theme's notes cite
  Peter parking it; delete a retired theme that has no analyzer to re-seed
  it (e.g. `grandfathered-hum0024-nav-strip` — its FK-constraint residue is
  destructive schema work, a GH issue per D9, not sweep work).

**Cap:** this rung never fully drains, so it could otherwise eat the whole
night and starve every rung below it. Spend at most ~15% of tonight's budget
here (stated at the top of `daily-debt.md`) — once you've spent that long,
stop and descend to rung 2 regardless of remaining candidates.

**Drained:** never fully — always safe to spend a few minutes here before
descending. Skip the pass only if you already checked every seed item above
in a prior run and no new rows have been added since.

**Done-check:** `docs/architecture/debt-ledger.yml` and touched `debt.yml`
files still parse as valid YAML (`python3 -c "import yaml,glob; [yaml.safe_load(open(f)) for f in glob.glob('docs/architecture/debt-ledger.yml') + glob.glob('src/Sections/*/Docs/debt.yml')]"`). No build/test needed (docs-only).

---

## Rung 2 — Analyzer-config truth

**What:** `Directory.Build.props`'s `WarningsNotAsErrors` carries ids
that have met their own documented exit condition (last `[Grandfathered]`
gone): `HUM0009`, `HUM0014`, `HUM0017`, `HUM0018`, `HUM0019`, `HUM0025`.
Delete them. Separately, `NoWarn` hides `HUM_USER_NORMALIZEDEMAIL` entirely —
move it to `WarningsNotAsErrors` so the count becomes visible again (issue
nobodies-collective/Humans#635). **One-time — drains permanently** once done;
don't reopen unless a future sweep re-adds one of these ids.

**Finds:**
```
grep -n "WarningsNotAsErrors\|NoWarn" Directory.Build.props
grep -rc -A3 --include='*.cs' '^[[:space:]]*\[Grandfathered(' src | grep -oE 'HUM(0009|0014|0017|0018|0019|0025)' | sort -u
```
If the second command prints nothing for one of these ids, it's removable.
Confirm `HUM_USER_NORMALIZEDEMAIL` is still in `NoWarn` before moving it.

**Drained when:** none of these ids remain in `WarningsNotAsErrors` and
`HUM_USER_NORMALIZEDEMAIL` is no longer in `NoWarn`.

**Done-check:** `dotnet build Humans.slnx -v quiet -clp:ErrorsOnly --no-incremental` — green.
If removing an id turns the build red, that id was load-bearing (a grandfather
exists you didn't find) — revert just that one id and record it as a ledger
staleness finding for rung 1, not a rung-2 failure.

---

## Rung 3 — Root-cause rows over symptom rows

**What:** Some ledger rows are one instance of a repeating pattern. Fixing
the pattern's cause (or batching every instance in one themed PR) beats
burning nights on instances one at a time. A sibling process is adding a
`root:` link to symptom rows pointing at their cause — once present, prefer
any `root:` cluster with ≥2 live members over an isolated row.

**Root A — doc/comment/freshness drift — drained.** The freshness-script
`mawk`/`asorti` bug is fixed and no `root: CENTRAL-57` symptom rows remain.
A write-time drift detector is a CI/check proposal — close such rows outright
(D5), don't build one. Batch any newly found stale-doc rows as one-line text
fixes (no `src/` change).
```
grep -rn "root: " docs/architecture/debt-ledger.yml src/Sections/*/Docs/debt.yml
```

**Drained when:** the grep above returns no `root:` cluster with ≥2 live
members. Coverage-only clusters are not nightly work.

**Done-check:** the build/test gate of whatever the cluster fix touched; doc
corrections require no build.

---

## Rung 4 — Localization gaps

**What:** A user-facing string hardcoded in English instead of a resx key,
on a route that is **not** admin/operator-exempt
(`memory/code/localization-admin-exempt.md`). All six cultures (en, es, de,
it, fr, ca) in the same commit. Collect a small batch of verified gaps
across related views/actions before editing; do not test and commit each
string or file separately. Follow `daily-debt.md`'s batching rules.

**Finds:**
```
grep -rn 'SetError("\|SetSuccess("\|SetInfo("' src/Sections/*/Controllers/*.cs | grep -v 'Localizer\['
# `**` is NOT recursive here: bash leaves `globstar` off by default, so it
# matches one level and silently skips the nested views. Use find.
find src/Sections/*/Views -name '*.cshtml' -print0 \
  | xargs -0 grep -LZ 'Localizer\[' | xargs -0 -n1 dirname | sort -u
```
Cross-check each hit's controller/view against the exempt route list before
picking it.

**Seed check (2026-10-10):** The former Teams edit, Budget views, Events forms/card and Users attendance seeds are already localized. Teams edit is also operator-exempt by its actual policy. Re-run Finds for current gaps, including enum labels and refusal feedback.

**Drained when:** both grep commands return nothing outside the exempt list.

**Done-check:** `dotnet test tests/Humans.Web.Tests -v quiet -clp:ErrorsOnly --filter "SharedResourceParityTests|SectionResourceParityTests"` plus every affected section's tests, once for the completed batch.

---

## Rung 5 — DisplaySort baseline (actionable view-moves only)

**What:** `tests/Humans.Web.Tests/Architecture/Baselines/DisplaySortInControllers.baseline.txt`
mixes two things. The `TicketRepository` lines are ones **Peter ruled stay**
(needs the paged grid's server-side sort redesigned first) — never touch
those. The rest are real, scoped refactors: move an `OrderBy`/`OrderByDescending`
out of the repository into its rendering consumer. See the
`baseline-display-sort` ledger theme's `baseline_entries`/`not_actionable`/
`remaining` fields for the current split.

**Finds:**
```
grep -v TicketRepository tests/Humans.Web.Tests/Architecture/Baselines/DisplaySortInControllers.baseline.txt
```
For each line: find the method's caller(s) — **read the consumer first**,
don't just delete the sort. A prior PR (peterdrier/Humans#1002) had to revert two premature
deletions where the view rendered in repo order with no re-sort.

**Seed:** the `TeamRepository` rows (`GetAllRoleDefinitionsAsync`). Add a
`SortOrder` field to `TeamRosterSlotSummary` and sort in the consumer's VM
path. `TeamRosterSlotSummary` is `internal sealed`
(`ITeamManagementService.cs`), so this is not public surface (D2) — do it.

**Drained when:** the grep above (minus TicketRepository) returns nothing.

**Done-check:** `dotnet test tests/Humans.Web.Tests -v quiet -clp:ErrorsOnly --filter DisplaySortInControllers` — baseline shrinks by exactly the lines you removed, nothing else changes.

---

## Rung 6 — Dead-or-duplicate code

**What:** Confirmed-dead code (zero call sites, a duplicate private helper,
an unused enum flag) with no public-surface change. A "duplicate mutation
pipeline" collapsed into one owner-section method replacing ≥2 identical
caller sequences is pre-approved (D1) — do it and list it in the PR, as is a section-internal
split into a new `internal` class (D1). Only a "god class" split needing a NEW interface or service goes to Peter, as a D3
yes/no proposal with the concrete signatures.

**Finds:** `review: light` rows mentioning dead/duplicate code:
```
grep -B1 -A1 "review: light" docs/architecture/debt-ledger.yml src/Sections/*/Docs/debt.yml \
  | grep -i "dead\|duplicate\|unused\|zero call sites\|no caller"
```

**Seed check (2026-10-10):** The former duplicate ticket cache helper, PriorityOnly flag, reportLink parameter and Login_Hello key seeds no longer exist. Re-run Finds for current production duplication.

**Drained when:** the grep above returns nothing with `review: light`.

**Done-check:** `dotnet test tests/Humans.<Section>.Tests -v quiet -clp:ErrorsOnly`.

---

## Rung 7 — Naming / resource-key prefixes

**What:** Resx keys missing their section's `<Section>_` prefix. Both known
instances (`GovernanceResource` — 85% of keys, `TeamsResource` — 90%) are
large, six-culture, every-view-binding renames — the auditors themselves
flagged both as report-only, not self-contained. Expect to skip most nights;
only take one if it can land as a single-section, same-commit rename (old
key deleted, new key + every usage site + all six cultures updated together)
that finishes inside the budget. Otherwise skip-and-note.

**Finds:**
```
for f in src/Sections/*/Resources/*Resource.resx; do
  section=$(basename "$f" Resource.resx)
  total=$(grep -c '<data name=' "$f")
  prefixed=$(grep -c "<data name=\"${section}_" "$f")
  echo "$section: $prefixed/$total prefixed"
done
```

**Drained when:** no section's ratio is low enough to fix in one session, or
every remaining gap is already report-only per a prior night's note.

**Done-check:** `dotnet test tests/Humans.Web.Tests -v quiet -clp:ErrorsOnly --filter "SharedResourceParityTests|SectionResourceParityTests"` plus a full-text search confirming no orphaned old key remains anywhere in `src/`.

---

## Rung 8 — Real TODO/HACK compromises

**What:** `MA0026` is globally `NoWarn`'d, so TODO/HACK comments don't fail
the build — the seed below are the genuine accepted compromises left, per
a full audit; everything else that greps as TODO/HACK is a note-to-self, not
debt. New ones can appear over time — **this rung never fully drains either**,
same as rung 1, but starts nearly empty.

**Finds:**
```
grep -rn "TODO\|HACK" --include=*.cs --include=*.cshtml src/Sections/Humans.Calendar src/Sections/Humans.Tickets
```
(plus a periodic full-`src/` sweep to catch new ones elsewhere — read each
hit; most are legitimate notes-to-self, not fixable debt.)

**Seed check (2026-10-10):** The former Calendar URL/organization-default and ticket-stub event-label seeds are fixed. Feed links use configured public base URLs and the ticket stub reads active event settings. Re-run Finds for new compromises.

**Drained when:** the seed items are closed and the current sweep
finds no new genuine compromise (a debug-screen "pending caching" comment
etc. is not one — leave it).

**Done-check:** `dotnet test tests/Humans.<Section>.Tests -v quiet -clp:ErrorsOnly` for whichever section changed.

---

## Permanently off the ladder

- **Not debt (policy D6–D9) — close such rows, never add them:** tests that
  merely pin behaviour or inventory missing tests; analyzer/inventory checks
  for judgment calls; admin-timed operational races and slow-but-working
  admin pages; destructive schema work (GH issue). Integration-test
  flakiness is a GH issue. Hangfire compat shims/type remaps/migrations
  (D13): jobs re-register at startup — delete the shim, never add one.
- **Standalone test work:** missing coverage, controller-policy pins, test
  scaffolding, test helpers, and test-suite expansion are not sweep objectives.
  Add or update a focused test when a production fix warrants it, as part of
  that fix. Coverage gaps alone are not candidates, even when other work takes
  more investigation. Do not fill the time window with standalone test work.

Never pick these up unattended — state the reason if you notice them so a
future run doesn't re-derive it:

- **Entity-to-DTO return types** (`ApplicationServiceEntityReadReturns.baseline.txt`).
  Peter: "one refactor, outside the tech debt nightly process." Every
  entry is a public interface return type — changes public surface.
- **HUM0028 cache invalidators** (the invalidator family; `IUserInfoInvalidator`
  is unmarked and invisible to the analyzer). Peter: "I don't think fixing all
  of them in one go is possible... that won't be a short fix." A 2026-06-13
  audit found folding them into decorators breaks read-after-write ordering.
  Parked.
- **`NoDestructiveMigrationOps.baseline.txt`.** An approval ledger of
  immutable, already-shipped migrations with Peter's written sign-off above
  each — not a backlog. The rule guards new drops, never sweep existing ones.
- **The `[DontFix]` classes** (`AuditLogService`, `RoleAssignmentService`).
  Peter-only, permanent. Never "fix" them, never add the attribute elsewhere.
- **The `TicketRepository` rows in `DisplaySortInControllers.baseline.txt`.**
  Peter ruled these stay until the paged grid's sort is redesigned
  server-side. Not markable, not a rung-5 target.
- **`HUM0010`/`HUM0011`** in `WarningsNotAsErrors`. The `[ExpiresOn]` staged
  escalation depends on these staying warnings pre-deadline — do not remove
  alongside rung 2's ids.
- **`HUM0031`** and anything under a `parked:` key in `debt-ledger.yml`.
  Peter froze `grandfathered-hum0031-controller-logic` 2026-08-07 pending
  the per-section assembly split; don't lower thresholds or de-grandfather.
