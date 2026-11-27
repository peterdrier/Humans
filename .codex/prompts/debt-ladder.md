# Nightly Debt Ladder

A standing, top-down list of **work types**, not a nightly checklist. Open
ledger rows come first: the rungs order and explain the work on them, and
their Finds commands look for new debt only once no ledger row is actionable.
The ledger must end every run smaller than it started (`daily-debt.md`). Make a
brief rung-1 hygiene pass, then work substantive rungs top-down. After every
fix, continue with another candidate from the ledger or a rung's Finds. When
none is left, stop, even before the deadline (`daily-debt.md`, **Stop when the
work runs out**). One run produces one PR containing all fixes.

Guardrails, working loop, and the timed-goal/commit protocol live in
`daily-debt.md`. This file is only the priority order and the per-rung
mechanics: what it is, how to find tonight's candidates, how to tell the rung
is drained, and the done-check.

**Every rung obeys `daily-debt.md`'s guardrails without exception** — and
`memory/process/debt-sweep-standing-policy.md` (D1–D13): rule-prescribed
contract changes are pre-approved and listed in the PR (D1), `internal` types
are never public surface (D2), a new interface/service/endpoint still goes to
Peter in D3 format, no category-bin skips (D4); one coherent fix or
same-correction batch per commit (possibly across sections), every culture,
migrations only via `dotnet ef migrations add`, never touch `NoDestructiveMigrationOps.baseline.txt` or a `[DontFix]` class.

Every ledger row carries a permanent `id:` (`memory/process/debt-ledger-additions.md`). Identify
an item by its id everywhere — in the PR body, in commit messages — and
by `file:line` only for a baseline/props entry, which has no ledger row. The PR body **must** list
the ids it closed.

Seeds and counts are not facts (D10): re-derive them from each rung's Finds
every night. Once a rung's Finds return nothing, it is drained.

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

Theme bookkeeping (D11): re-run each theme's own detector and write what it
returns into that theme's `remaining:`; never carry a number in this file
(`memory/process/no-derived-aggregates-in-docs.md`). Add a missing `parked:`
key where a theme's notes cite Peter parking it; delete a retired theme that has
no analyzer to re-seed it.

**Cap:** this rung never fully drains, so it could otherwise eat the whole
night and starve every rung below it. Spend at most ~15% of tonight's budget
here (stated at the top of `daily-debt.md`) — once you've spent that long,
stop and descend to rung 2 regardless of remaining candidates.

**Drained:** never fully — always safe to spend a few minutes here before
descending. Skip the pass when no row has been added or changed since the last run.

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
(`memory/code/localization-admin-exempt.md`). All supported cultures (en, es, de,
it, fr, ca, nl) in the same commit. Collect a small batch of verified gaps
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
Collect the eligible moves in the section before editing, inspect every consumer,
and validate them as one batch under `daily-debt.md`. Do not create one round per
`OrderBy`; explain splits caused by different semantics or risk.


**Drained when:** the grep above (minus TicketRepository) returns nothing.

**Done-check:** `dotnet test tests/Humans.Web.Tests -v quiet -clp:ErrorsOnly --filter DisplaySortInControllers` — baseline shrinks by exactly the lines you removed, nothing else changes.

---

## Rung 6 — Dead-or-duplicate code

**What:** Confirmed-dead code (zero call sites, a duplicate private helper,
an unused enum flag) with no public-surface change. A "duplicate mutation
pipeline" collapsed into one owner-section method replacing ≥2 identical
caller sequences is pre-approved (D1) — do it and list it in the PR. Never split a large
service ([`one-service-class-no-helpers`](../../memory/architecture/one-service-class-no-helpers.md)):
a helper it constructs and forwards to is forbidden, and a split into separate services is
Peter's call, so propose it in the report instead. An existing helper moves back into its service.

**Finds:** `review: light` rows mentioning dead/duplicate code:
```
grep -B1 -A1 "review: light" docs/architecture/debt-ledger.yml src/Sections/*/Docs/debt.yml \
  | grep -i "dead\|duplicate\|unused\|zero call sites\|no caller"
```


**Drained when:** the grep above returns nothing with `review: light`.

**Done-check:** `dotnet test tests/Humans.<Section>.Tests -v quiet -clp:ErrorsOnly`.

---

## Rung 7 — Naming / resource-key prefixes

**What:** A deliberate, single-section resource-key prefix cleanup under
`memory/code/resource-key-prefix-matches-section.md`. Existing keys are not
renamed as a side effect of unrelated work. Keep reflection-resolved
`Enum_{TypeName}_{Value}` keys and the approved `Issue_` prefix unchanged.
Update every usage and all supported cultures together; only take a candidate that
can finish as one coherent change inside the remaining window.


**Finds:** Resources live at each section project's root, not in a `Resources/`
subdirectory. Inspect each result against the naming atom before selecting it.
```
for f in src/Sections/*/*Resource.resx; do
  section=$(basename "$f" Resource.resx)
  total=$(grep -c '<data name=' "$f")
  prefixed=$(grep -c "<data name=\"${section}_" "$f")
  echo "$section: $prefixed/$total prefixed (includes exempt keys)"
done
```

**Drained when:** no remaining candidate fits a deliberate single-section
change after accounting for the documented exemptions and every usage site.

**Done-check:** `dotnet test tests/Humans.Web.Tests -v quiet -clp:ErrorsOnly --filter "SharedResourceParityTests|SectionResourceParityTests"` plus a full-text search confirming no orphaned old key remains anywhere in `src/`.

---

## Rung 8 — Real TODO/HACK compromises

**What:** `MA0026` is globally `NoWarn`'d, so TODO/HACK comments don't fail
the build. Most TODO/HACK comments are notes-to-self, not debt; only a genuine
accepted compromise is a candidate.

**Finds:**
```
grep -rn "TODO\|HACK" --include=*.cs --include=*.cshtml src/Sections/Humans.Calendar src/Sections/Humans.Tickets
```
(plus a periodic full-`src/` sweep to catch new ones elsewhere — read each
hit; most are legitimate notes-to-self, not fixable debt.)


**Drained when:** the current sweep
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
- **Invented validation and plumbing tests** ([`no-invented-validation`](../../memory/code/no-invented-validation.md),
  [`cancellation-token-propagation`](../../memory/architecture/cancellation-token-propagation.md)):
  guards for states nothing produces, tolerance for malformed admin settings,
  and tests that cancel a read to prove the token reaches it. Never a fix;
  remove such code when a ledger row asks for it.
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
- **The `[DontFix]` classes** (`AuditLogService`).
  Peter-only, permanent. Never "fix" them, never add the attribute elsewhere.
- **The `TicketRepository` rows in `DisplaySortInControllers.baseline.txt`.**
  Peter ruled these stay until the paged grid's sort is redesigned
  server-side. Not markable, not a rung-5 target.
- **`HUM0010`/`HUM0011`** in `WarningsNotAsErrors`. The `[ExpiresOn]` staged
  escalation depends on these staying warnings pre-deadline — do not remove
  alongside rung 2's ids.
- **`HUM0031`'s thresholds** and anything under a `parked:` key in
  `debt-ledger.yml`. The 40/15 numbers are fixed permanently (Peter,
  2026-10-21); controller intent is section-doctor's call. Never move them.
