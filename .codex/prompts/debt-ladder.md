# Nightly Debt Ladder

A standing, top-down list of **work types**, not a nightly checklist. Try rung 1;
if it has no available work tonight, say so and try rung 2; and so on. As top
rungs drain they stop producing work and lower rungs take over — nobody needs
to re-tune this file as the corpus shrinks.

Guardrails, working loop, and the SIGINT/commit protocol live in
`daily-debt.md`. This file is only the priority order and the per-rung
mechanics: what it is, how to find tonight's candidates, how to tell the rung
is drained, and the done-check.

**Every rung obeys `daily-debt.md`'s guardrails without exception** — public
surface needs Peter's approval (skip, don't add it), one section/theme per
commit, six cultures, migrations only via `dotnet ef migrations add`, never
touch `NoDestructiveMigrationOps.baseline.txt` or a `[DontFix]` class.

Every ledger row carries a permanent `id:` (`memory/process/debt-ledger-additions.md`). Identify
an item by its id everywhere — in the PR body, in commit messages, in this file's seed lists — and
by `file:line` only for a baseline/props entry, which has no ledger row. The PR body **must** list
the ids it closed.

**Seed lists below are dated 2026-09-20** (from a full-corpus audit). Verify
each is still true before touching it — code moves fast and a seed item that's
already fixed is rung-1 work (close it), not rung-N work. Once a rung's seed
list is exhausted, fall back to its **Finds** command for anything filed since.

---

## Rung 1 — Ledger hygiene

**What:** Close `inbox:`/`themes:` rows in `docs/architecture/debt-ledger.yml`
and `src/Sections/*/Docs/debt.yml` whose described defect the code has since
fixed. Pure deletion/correction of ledger text — no `src/` change, so no build
or test gate. **Recurring — never fully drains**, because new rows go stale
over time too. Do this first every night, cheaply, before picking a "real"
rung; it costs little and keeps every other rung's Finds command honest.

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
  no longer exists and its share of that count went with it. Also rewrite
  the `grandfathered-hum0024-nav-strip` note: the C#-side navs are gone (the
  G5 split moved every config into its owning section) and the only residue
  is cross-section FK constraints in the schema, which is separate migration
  work, not sweep work.

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

**Root A — doc/comment/freshness drift after a section move or read-split.**
Several ledger rows, same cause: nothing re-verifies a doc or freshness-trigger
block when the code it describes moves. The actual root-cause *bug*, not
just the symptom pattern: `docs/scripts/freshness-checks/dependency-graph.sh`
uses `asorti` (gawk-only) but the runner's `awk` is `mawk` — the script dies
mid-run and still prints `PASS`, so freshness drift is going undetected
today. Fix that script first (swap `asorti` for a portable sort) — it's the
lever that catches future instances of this whole row-class automatically.
```
bash docs/scripts/freshness-checks/dependency-graph.sh   # reproduces the false PASS
```
Then, in the *same* PR if time allows (same theme: "doc freshness"), close a
batch of the stale-doc rows themselves — they're independent one-line text
fixes, safe to batch because none touch `src/`. The cluster is linked by
`root: CENTRAL-57` — grep that directly rather than the word "freshness",
which also matches unrelated Gdpr/Tickets ledger rows and misses the linked
Development ledger row:
```
grep -rln "root: CENTRAL-57" docs/architecture/debt-ledger.yml src/Sections/*/Docs/debt.yml
```
Seed instances (2026-09-20, verify first): `docs/guide/Glossary.md` (no
"assembly vote" entry), `dependency-graph.md` (duplicate `classDef monitor`;
misclassifies Backdoor as a crosscut; names non-existent `TicketVendorService`),
`freshness-catalog.yml` (missing Settings entry; missing Backdoor entry; no
`Section.cs` trigger), `debt-ledger.yml`'s own HUM0028 note ("EF interceptor"
→ direct `InvalidateAll()` calls since nobodies-collective/Humans#751), `G5-SECTION-TEMPLATE.md` (wrong
`EmailRenderer` path; wrong "design §8" citation in several `Section.cs` files),
`design-rules.md` (only one GDPR download route named; wrong thread-safety
reason; wrong contributor mechanism/`AgentService` example), `.claude/skills/test-site/SKILL.md`
(stale `/Profile/Emails` routes), `.claude/skills/section-align/SKILL.md`
(wrong "Gdpr has no Controllers/" claim), `section-conformance.yml` (stale
"no Docs/" note for Settings/TicketTailor).

**Root B — sections with no controller-test layer.** Several rows share this
cause (Governance's two controller-authz-unpinned rows collapse into one fix
here). Stand up the missing `tests/Humans.<Section>.Tests/Controllers/`
skeleton for one section, pinning `[Authorize(Policy = ...)]` placement —
that single file closes every row filed against that section's controllers.
```
for d in tests/Humans.*.Tests; do
  find "$d" -iname '*Controller*' | grep -q . || echo "NO CONTROLLER TESTS: $d"
done
```
Seed: `tests/Humans.Governance.Tests` (zero controller tests — closes both
Governance controller-authz rows in one file), `tests/Humans.Containers.Tests`
(zero controller tests). This cluster is linked by `root: CENTRAL-56` — once a
section's test skeleton lands, close the rows this grep finds for it:
```
grep -rln "root: CENTRAL-56" docs/architecture/debt-ledger.yml src/Sections/*/Docs/debt.yml
```

**Drained when:** the freshness script fix has landed, root-A's grep returns
no live rows, and root-B's `for` loop and grep above both print nothing.

**Done-check:** root A — docs-only, no build; rerun the freshness script and
confirm it no longer dies under `mawk`. Root B — `dotnet test tests/Humans.<Section>.Tests -v quiet -clp:ErrorsOnly`.

---

## Rung 4 — Missing tests, self-contained

**What:** Pin an already-correct invariant with a test. No behavior change,
no new public surface — pure test addition. Excludes rung 3's controller-test
root cluster and rung 5's authorization-attribute items (those are their own
rungs even though they're also "a missing test").

**Finds:** ledger rows tagged `review: light` whose `what:` text uses
missing-coverage language, minus anything needing a design call:
```
grep -B1 -A1 "review: light" docs/architecture/debt-ledger.yml src/Sections/*/Docs/debt.yml \
  | grep -i "untested\|unpinned\|no test\|not covered\|zero hits\|no coverage"
```
Read the full row before picking — skip anything whose `what:` mentions
"Peter's call", a needed fixture/harness the section doesn't have, or a
cross-section blocker.

**Seed (self-contained, smallest first):**
- `Humans.Backdoor` — `BackdoorApiKeyRepository` has no test file at all;
  pin `FindActiveByHashAsync` excludes revoked, `RevokeAsync` is idempotent.
- `Humans.Gdpr` — `GdprService` logs the failing contributor's name before
  rethrow, but the test fixture injects `NullLogger`; swap in a capturing
  logger and assert the log content.
- `Humans.Governance` — `ApplicationDecisionService.ScrubFreeTextForUserAsync`
  (Art. 17 erasure) has zero test coverage anywhere.
- `Humans.Governance` — `ApproveAsync`/`RejectAsync` never assert their
  `AuditLog.LogAsync` call; the one-vote-per-Board-member overwrite rule is
  unpinned at every layer, not just under-described as "repo-only."
- `Humans.Users` — `UsersAdminController` actions beyond the two purge
  negatives (`SuspendHuman`, `UnsuspendHuman`, `RejectSignup`, `Roles`,
  `Audience`) have zero tests.
- `Humans.Tickets` — `EraseForUserAsync`, `GetUserTicketExportDataAsync`,
  `GetMatchedTicketUserIdsAsync`/`GetMatchedUserIdsForEmailsAsync` untested;
  sync-removes-Ticketed-when-no-valid-ticket-remains path unpinned.
- `Humans.Rideshare` — five named `RideshareService` rule keys (capacity,
  non-Active refusal, seats-minimum, non-pending Accept/Decline, non-owner
  UpdateRequest) never asserted in `RideshareServiceTests`.
- `Humans.Surveys` — repo-level `ReminderSentAt` filter/stamp: no
  `SurveyRepository*Tests.cs` exists at all; the service-level sibling test
  (`SendDueRemindersAsync_sends_one_reminder_...`) shows the pattern to copy.
- `Humans.Gate` — the barcode-dedupe race needs a Postgres-backed test
  (EF InMemory can't raise 23505); use `Humans.Integration.Tests`' pattern.
- `Humans.Finance` — `Views/Finance/CreditorStatement.cshtml` flips the
  ledger-line sign in the view; no test pins the sign flip.
- `Humans.Campaigns` — `UpdateGrantEmailStatusAsync`/`GetCodeTrackingAsync`
  still uncovered (the other two named methods in this row are already
  fixed — narrow the row to just these two, don't re-add the fixed ones).

**Drained when:** the grep above returns nothing new and every seed item is
closed or reclassified (moved to a Peter's-call rung/off-ladder).

**Done-check:** `dotnet test tests/Humans.<Section>.Tests -v quiet -clp:ErrorsOnly`.

---

## Rung 5 — Authorization-unpinned tests (controller attribute placement)

**What:** A controller carries `[Authorize(Policy = ...)]` but no test pins
*which* policy — a silent downgrade (e.g. to a weaker policy) would pass
every existing test. Add the pin; don't change the attribute.

**Finds:** controllers absent from the central policy-pin theory data:
```
grep -rhoP '(?<=class )\w+Controller' src/Sections/*/Controllers/*.cs src/Humans.Web/Controllers/*.cs | sort -u > /tmp/all_controllers.txt
grep -oP '(?<=typeof\()\w+Controller' tests/Humans.Web.Tests/Authorization/EndpointAuthorizationTests.cs | sort -u > /tmp/pinned_controllers.txt
comm -23 /tmp/all_controllers.txt /tmp/pinned_controllers.txt
```
This is a coarse "any pin exists" check, not full-attribute coverage — read
each candidate's actions before deciding it's actually unpinned.

**Seed:** `GovernanceApplicationsController`, `GovernanceBoardVotingController`,
`GovernanceVotesAdminController` — likely already closed by rung 3's Root B
(the new `tests/Humans.Governance.Tests/Controllers/` file); check rung 3's
work before duplicating here.

**Drained when:** `comm` above prints nothing (every controller has at least
one policy-pin test), or every remaining name is a Shell/admin controller
already covered by an existing test the grep's regex missed (verify by hand).

**Done-check:** `dotnet test tests/Humans.Web.Tests -v quiet -clp:ErrorsOnly --filter EndpointAuthorizationTests` plus the touched section's own test project.

---

## Rung 6 — Localization gaps

**What:** A user-facing string hardcoded in English instead of a resx key,
on a route that is **not** admin/operator-exempt
(`memory/code/localization-admin-exempt.md`). All six cultures (en, es, de,
it, fr, ca) in the same commit.

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

**Seed:**
- `Humans.Teams` — `Views/Team/EditTeam.cshtml` (route `/Teams/{id}/Edit`,
  not exempt despite its elevated policy) has zero `Localizer[` calls;
  several literal strings ("Custom Slug", "Sensitive team", etc.).
- `Humans.Budget` — `Views/Budget/Index.cshtml` and `CategoryDetail.cshtml`
  have zero `Localizer[` calls; `Summary.cshtml` has one residual literal
  ("Expenses" button label) among otherwise-localized content.
- `Humans.Events` — `Views/Events/IndividualEventForm.cshtml` has zero
  `Localizer[` calls; `BarrioEventForm.cshtml` has exactly one, rest literal;
  `EventsCard/Default.cshtml` has 2 hardcoded strings beside localized ones.
- `Humans.Users` — `ProfileController`'s `DeclareNotAttending`/
  `UndoNotAttending`/related methods pass literal English to
  `SetSuccess`/`SetError` instead of `UsersResource` keys.

**Drained when:** both grep commands return nothing outside the exempt list.

**Done-check:** `dotnet test tests/Humans.Web.Tests -v quiet -clp:ErrorsOnly --filter "SharedResourceParityTests|SectionResourceParityTests"` plus the section's own tests.

---

## Rung 7 — DisplaySort baseline (actionable view-moves only)

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

**Seed — doable tonight without new public surface:** `GoogleResourceRepository`,
`CampRepository.Roles.cs`, `CampaignRepository`.

**Blocked, skip-and-note (needs Peter's approval, not tonight's job):** the
`TeamRepository` rows need a `SortOrder` field added to
`TeamRosterSlotSummary` — that's new public DTO surface. Skip per
`daily-debt.md`'s public-surface rule; note it so nobody re-derives this.

**Drained when:** the grep above (minus TicketRepository, minus the
TeamRepository rows) returns nothing.

**Done-check:** `dotnet test tests/Humans.Web.Tests -v quiet -clp:ErrorsOnly --filter DisplaySortInControllers` — baseline shrinks by exactly the lines you removed, nothing else changes.

---

## Rung 8 — Dead-or-duplicate code

**What:** Confirmed-dead code (zero call sites, a duplicate private helper,
an unused enum flag) with no public-surface change. Larger "god class" /
"duplicate mutation pipeline" rows are Peter's-call — skip those, they need
a public interface reshape.

**Finds:** `review: light` rows mentioning dead/duplicate code:
```
grep -B1 -A1 "review: light" docs/architecture/debt-ledger.yml src/Sections/*/Docs/debt.yml \
  | grep -i "dead\|duplicate\|unused\|zero call sites\|no caller"
```

**Seed:**
- `Humans.Camps` — `UserInfoStubHelpers.MakeUserInfo` is dead;
  `CampControllerTests` defines its own private copy.
- `Humans.Tickets` — `CachingTicketQueryService` defines `WithInner` twice
  (outer class + nested `UserHoldingsCache`), same name and shape.
- `Humans.Shifts` — `ShiftBrowseQueryFlags.PriorityOnly` is read once, set
  nowhere — confirmed dead flag.
- `Humans.MailerLite` — `MailerLiteDateConverter` is registered but nothing
  routes through it (hand-rolled `ParseDate` duplicates its logic instead).
- `Humans.Email` — `reportLink` is a dead parameter end-to-end in the
  feedback-reply email (resx uses `{0}`–`{2}`, renderer passes an unused `{3}`).
- Central `inbox:` — `Login_Hello` resx key named in no view/controller/tag
  helper across any culture file.

**Drained when:** the grep above returns nothing with `review: light`.

**Done-check:** `dotnet test tests/Humans.<Section>.Tests -v quiet -clp:ErrorsOnly`.

---

## Rung 9 — Naming / resource-key prefixes

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

## Rung 10 — Real TODO/HACK compromises

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

**Seed:**
- `Humans.Calendar/Contracts/CalendarFeedItem.cs:32` — hardcoded production
  base URL for feed deep links. Move to configuration.
- `Humans.Calendar/Controllers/CalendarController.cs:425` — hardcoded "all
  volunteers in Spain" org default. Derive from settings/profile instead.
- `Humans.Tickets/Views/Shared/Components/TicketStub/Default.cshtml:9` —
  event label from a constant instead of the active event.

**Drained when:** the seed items are closed and the current sweep
finds no new genuine compromise (a debug-screen "pending caching" comment
etc. is not one — leave it).

**Done-check:** `dotnet test tests/Humans.<Section>.Tests -v quiet -clp:ErrorsOnly` for whichever section changed.

---

## Permanently off the ladder

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
  server-side. Not markable, not a rung-7 target.
- **`HUM0010`/`HUM0011`** in `WarningsNotAsErrors`. The `[ExpiresOn]` staged
  escalation depends on these staying warnings pre-deadline — do not remove
  alongside rung 2's ids.
- **`HUM0031`** and anything under a `parked:` key in `debt-ledger.yml`.
  Peter froze `grandfathered-hum0031-controller-logic` 2026-08-07 pending
  the per-section assembly split; don't lower thresholds or de-grandfather.
