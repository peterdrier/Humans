# Users decomposition — single-PR move plan

**Status:** agreed with Peter 2026-10-19; ready to run in a fresh session with `/pd:orch`.
**Scope:** one branch, one PR, one commit per move. Nothing here drops a column or a table; every drop is a
later, evidence-gated PR driven by the data-migration status endpoint (§3.3).

## 1. Why

`Humans.Users` (+ `.Contracts`) is 28,000 source lines (22,400 C#, 5,600 Razor; 8,400 lines of migrations
excluded) and 2,714 of the solution's 13,923 reforge points. About 7,500 of those lines are other sections'
data or screens that landed on `User`/`Profile`/`UserEmail` before sections could own person-keyed tables.
The rest (identity, addresses, merge, deletion, profile core, the `UserInfo` cache) is Users' own lane and stays.

Decisions taken with Peter (chat, 2026-10-19): communication preferences → **Email**; **medical conditions stay
in Users** (Art. 9, gated by `MedicalDataViewer`); privacy page → **Consent** (owner of `legal_documents`);
deletion → **Gdpr**; Google address flags → **GoogleIntegration**; audience segmentation stays until a
vendor-agnostic mailing-list section exists; whole tables move by **new table + copy**, never by table
transfer; all copies run from **one admin screen** with a **public status endpoint** (§3.3).

## 2. The moves

Line counts are estimates from file sizes plus line ranges inside the shared files (`UserService`,
`ProfileController`, `Edit.cshtml`, `UserRepository`, `CachingUserService`, `AdminDetail.cshtml`,
`UserEmailService`, `ProfileEmailsController`). "Kind": **view** = screens and view models only, data stays
in Users; **columns** = fields on a Users table become a new table in the target; **table** = a whole Users
table is re-created in the target and copied.

| # | Move | Target | Kind | Src LOC | Test LOC | Risk |
|---|---|---|---|---|---|---|
| 1 | IBAN, `RevealIban`, Payment Details card | Finance | columns | ~120 | ~50 | low |
| 2 | Campaign codes on profile and admin detail | Campaigns | view | ~80 | ~30 | low |
| 3 | Outbox pages (own, admin) | Email | view | ~110 | ~30 | low |
| 4 | Role roster, Add/End role, role card | Auth | view | ~430 | ~150 | low |
| 5 | Admin debug grid | Debug | view | ~195 | ~140 | low |
| 6 | Shift-preference card inside `/Profile/Me/Edit` | Shifts | view | ~90 | ~50 | low |
| 7 | Facilitated messaging (`/Profile/{id}/SendMessage`) | Email | view | ~350 | ~155 | low |
| 8 | Deletion: request, status, `DownloadData` | Gdpr | view | ~300 | ~100 | medium |
| 9 | Privacy page shell | Consent | view | ~60 | — | low |
| 10 | Dietary preference, allergies, intolerances, "other" text, dietary nudge | Cantina | columns | ~500 | ~350 | medium |
| 11 | Membership tier (+ term dates), tier badge, apply block in the editor | Governance | columns | ~450 | ~200 | medium |
| 12 | Consent-check review (status, at, by, notes), rejection fields, `IsApproved` | Consent | columns | ~600 | ~1,850 | high |
| 13 | Event participation | Tickets | table | ~800 | ~350 | medium |
| 14 | Communication preferences, `MessageCategory`, unsubscribe, Guest dashboard | Email | table | ~2,100 | ~1,270 | medium |
| 15 | Google workspace identity: `IsGoogle`, per-address `GoogleEmailStatus`, Set/ClearGoogle, Google Sync card, nobodies.team badge | GoogleIntegration | columns | ~600 | ~400 | high |
| 16 | Cleanup: dead readers, docs, ledger, reforge reading | Users | — | — | — | low |

Totals: ~6,800 source lines leave Users, ~5,100 test lines follow. Expected reforge: Users ~1,700
(from 2,714); Email, Tickets, Governance, Consent, Cantina, GoogleIntegration each gain 100 to 400.

**Stays in Users, on purpose:** medical conditions (Art. 9; gets its own small `/Profile/Me/Medical` form
when the dietary page leaves), emergency contact, languages, burner CV, contact fields, profile pictures,
location, people search, account merge and duplicate detection, deletion timestamps and the anonymize job,
audience segmentation (issue to file: vendor-agnostic mailing-list section over MailerLite, which takes
`UsersAudienceService` and `/Users/Admin/Audience` with it).

**Delete readers, keep columns:** `User.SuppressScheduleChangeEmails`, `User.UnsubscribedFromCampaigns`,
`User.GoogleEmailStatus` have no reader outside Users' own cache plumbing. Commit 16 removes the readers
and records each column in the debt ledger for an evidence-gated drop PR.

## 3. Mechanics every move shares

- **Reads** from the new owner go through its own `I<Section>Service` (cached, whole table in RAM via
  `TrackedCache`, like `CachingUserService`). The slice leaves `UserInfo`/`ProfileInfo`.
- **Writes** from other sections go through the owner's `I<Section>Write`; the matching method leaves
  `IUserWrite`/`IUserManagement`.
- **Screens** land in the owner's own controller under its own route, or stay on the Users page through
  a `UserPart` (`IUserPart` + `UserPartSlots.Profile` / `ProfileSidebar` / `AdminDetail` /
  `AdminDetailSidebar`; Tickets and Calendar already do this). A moved page takes the owner's own URL
  (`/<Section>/...`, admin pages `/<Section>/Admin/...`), with no redirect or alias from the old one
  ([`moved-pages-take-owner-urls`](../../../memory/product/moved-pages-take-owner-urls.md)); only a URL
  baked into sent emails or an external system stays, pending Peter's call.
- **Merge and GDPR:** the owner registers an `IUserMerge` (fold on account merge) and an
  `IUserDataContributor` (export + erase) for its new table; the slice leaves `UserService.ContributeForUserAsync`.
- **Audit:** every write the owner performs on a member's behalf keeps its audit entry (same
  `AuditAction`, moved call site).
- **Localization:** moved member-facing keys move resx-to-resx in all six cultures
  ([`resx-structure-aware-edits`](../../../memory/code/resx-structure-aware-edits.md)); admin-only
  screens need none.
- **Docs:** each commit updates `Docs/<Target>.md`, `Docs/data-access.md` and the matching
  rows in `Docs/Users.md` (data model, routing, actors, cross-section deps).
- **Tests:** the moved tests go to `tests/Humans.<Target>.Tests`; the commit gate is that project plus
  `tests/Humans.Users.Tests` plus `tests/Humans.Web.Tests` (architecture tests). Full
  `dotnet test Humans.slnx -v quiet` before the PR leaves draft.
- **Migrations:** one generated schema migration per target context per commit, reviewed by the
  `ef-migration-reviewer` agent before commit
  ([`ef-migration-review-gate`](../../../memory/process/ef-migration-review-gate.md)); snapshot diffed
  after every EF tool run. No data SQL, no hand edits
  ([`no-data-backfills`](../../../memory/process/no-data-backfills.md),
  [`no-hand-edited-migrations`](../../../memory/architecture/no-hand-edited-migrations.md)).

### 3.1 Moving data (#1, #10–#15)

Columns and whole tables take the same shape:

1. Target gets its table (`finance_payment_details`, `cantina_dietary_profiles`,
   `governance_membership_tiers`, `consent_reviews`, `tickets_event_participations`,
   `email_communication_preferences`, `google_workspace_identities`), keyed by `UserId`, no FK to `users`
   ([`no-cross-section-ef-joins`](../../../memory/architecture/no-cross-section-ef-joins.md)).
2. Writes go to the new table only. The legacy columns and tables stop being written
   ([`no-column-drops-for-decoupling`](../../../memory/architecture/no-column-drops-for-decoupling.md)).
3. The target registers a **data migration** (§3.3) that copies legacy → new. It reads the legacy values
   through `IUserService` from fields kept on `UserInfo`/`ProfileInfo` and marked `[Obsolete]` for exactly
   that caller.
4. Until the operator has run the copy, the owner's read falls back to the same obsolete field (same
   suppression). This keeps preview and QA correct without an operator step and is "self-healing on the
   read path", which the backfill rule allows.
5. When the status endpoint reports the migration done in production, a retirement PR deletes the
   migration, the fallback and the obsolete fields; a separate drop PR then removes the legacy columns
   or table. **The production `done` reading is the drop's approval** (Peter, 2026-10-19; recorded in
   [`no-drops-until-prod-verified`](../../../memory/architecture/no-drops-until-prod-verified.md),
   "Standing approval"): the baseline entry cites the reading and the merged retirement PR, and no
   per-case sign-off is needed.

Enum pinch: `MembershipTier` → `Governance.Contracts`, `ConsentCheckStatus` → Consent's contracts,
`ParticipationStatus`/`ParticipationSource` → `Tickets.Contracts`, `MessageCategory` → `Email.Contracts`,
`GoogleEmailStatus` → GoogleIntegration. `Users.Contracts` cannot reference Governance.Contracts or
Email.Contracts (both already reference it), so the obsolete legacy fields on `UserInfo`/`ProfileInfo`
keep **old** enum types renamed `Legacy*`, and the copy maps old → new. The `Legacy*` enums die with
the migrations. The `MessageCategory` move is a 21-section `using` sweep; no cycle, `Email.Contracts`
references only `Base` and `Users.Contracts`, and `Users.Contracts` drops its last use.

### 3.2 Google workspace identity (#15)

`user_emails.IsGoogle` names the one address Google Workspace knows a member by; `GoogleEmailStatus`
is Google's per-address verdict from sync. Both move to `google_workspace_identities`
(`UserId`, `Email`, `Status`, `UpdatedAt`; one row per user). The "at most one per user" invariant that
`UserEmailService.EnsureGoogleInvariantAsync` enforces on every email write becomes GoogleIntegration's
own invariant on its own table. Two seams stay in Users: when an address is deleted or unlinked, Users
calls `IGoogleIntegrationWrite.ClearIfAddressAsync(userId, email)` (Users already references
GoogleIntegration), and `UserInfo.GoogleEmail` is replaced by `IGoogleIntegrationService` reads at its
callers (Teams drive provisioning, Workspace admin, the nobodies.team badge). The Set/Clear Google
actions leave `ProfileEmailsController` for a GoogleIntegration controller under `/Profile/Me/Emails/Google`
and `/Profile/{id}/Admin/Emails/Google`; the Emails grid shows the flag through a GoogleIntegration
view component. `TrySetGoogleEmailStatusFromSyncAsync` leaves `IUserWrite`.

### 3.3 Data migrations: one screen, one public status endpoint, a permanent home

Peter's ask: never fourteen Temp screens again; one place to run copies, one unauthenticated endpoint a
development session can poll to learn whether production has finished them, and a permanent home whose
content is added, used and removed as migrations flow through.

**Home:** `Humans.Debug` ("any tool a developer wants", admin-only, owns no domain data, reachable in
production; Development is dev-only and therefore not it).

**Seam** (Base, the repo's contribution pattern,
[`section-contribution-seams`](../../../memory/architecture/section-contribution-seams.md)):

```csharp
public interface ISectionDataMigrations : ISectionContribution
{
    IEnumerable<DataMigration> Migrations();
}

public sealed record DataMigration(
    string Key,                                    // "finance.iban-to-payment-details"
    string Section,
    string Title,
    string Retires,                                // what the retirement PR deletes once done
    Func<IServiceProvider, CancellationToken, Task<DataMigrationStatus>> Status,   // pending count + every pending row
    Func<IServiceProvider, Guid, CancellationToken, Task<int>> Apply);            // actor, rows written
```

Each owning section implements the seam at its project root (`SectionDataMigrations : ISectionDataMigrations`)
and the delegates resolve that section's own services, so the copy itself stays inside the owner
and the host only calls the interface. Commit 0 re-homes the four existing Temp screens
(`/Profile/Admin/Backfill`, `/Profile/Admin/NameBackfill`, `/Profile/Admin/PictureMigration`,
`/Profile/Admin/EmailProblems/BackfillLegacyEmails`) as Users migrations and deletes their controllers
and views, so they get a done signal too.

**Screen:** `GET /Debug/DataMigrations` (`AdminOnly`): one row per registered migration with section,
title, pending count, the full list of pending rows (UserId plus the legacy values that will be
copied, as [`no-data-backfills`](../../../memory/process/no-data-backfills.md) requires: the operator
sees every row before confirming; the dataset fits in RAM, so no sampling or paging), and an
**Apply** button (`data-confirm`, `[ValidateAntiForgeryToken]`, audit-logged
`AuditAction.DataMigrationApplied` with key, actor, rows). Idempotent: a second apply finds zero
pending. Linked from the Debug admin nav.

**Endpoint:** `GET /api/data-migrations` (`[AllowAnonymous]`, mapped through `ISectionEndpoints` next to
the Shell's `/api/version` precedent):

```json
{ "version": "...", "migrations": [ { "key": "finance.iban-to-payment-details", "pending": 0, "done": true } ] }
```

Keys, pending counts and a boolean only; never row data. The counts are membership-shaped aggregates
(how many profiles still hold a legacy IBAN) and are judged safe to publish; say so in the Debug doc.

**Lifecycle:** a move commit adds its migration; QA and production operators apply it from the screen;
a development session polls `https://<prod>/api/data-migrations` and, when `done` is true everywhere it
matters, opens the retirement PR (delete migration, fallback, `[Obsolete]` fields) and then the drop PR,
for which the production `done` reading is the approval (§3.1 step 5). Follow-up outside this PR: a
`/migration-retire` skill that does the poll, the retirement PR and the drop PR.

## 4. Commit order

Ordered so every commit builds green and the branch is shippable at any cut. Framework first, cheap view
moves next (they establish the slots the data moves reuse), then the data moves by rising blast radius.

| Commit | Does | New references (cycle-checked) | Gate |
|---|---|---|---|
| 0 prep | §3.3 seam, Debug screen, endpoint, re-home the four Temp screens; `IAccountDeletionService` (and any other `Humans.Users/Contracts/*` interface a target needs) moves from the `Contracts/` folder to the `Users.Contracts` project; Debug doc gains the data-migrations section | Debug → Users (has) | Debug, Users, Web |
| 1 Finance | table, `IFinanceWrite.SetIban`, `RevealIban` under `/Finance/Admin/Members/{id}`, Payment Details as `AdminDetail` user part, export/erase/merge participants, migration `finance.iban-to-payment-details`, `SetProfileIbanAsync` leaves `IUserWrite`, Expenses/Holded read via `IFinanceService` | Expenses already → Finance.Contracts | Finance, Expenses, Users, Web |
| 2 Campaigns | "My codes" and "Campaign Codes" cards as `Profile`/`AdminDetail` user parts; `ICampaignService` leaves `ProfileController` and the admin builder | Campaigns → Users.Contracts (has) | Campaigns, Users |
| 3 Email outbox | `/Profile/Me/Outbox` and `/Users/Admin/{id}/Outbox` served by an Email controller; `IEmailOutboxService` leaves both Users controllers | Email → Users.Contracts (has) | Email, Users |
| 4 Auth roles | `/Users/Admin/Roles`, `AddRole`, `EndRole`, `_RolesListContent` move to Auth; role card becomes an `AdminDetail` user part | Auth → Users.Contracts (has) | Auth, Users |
| 5 Debug grid | `UsersAdminDebugController` + view + model → Debug | Debug → Users (has) | Debug, Users |
| 6 Shifts | shift-preference card leaves the Edit form (link to the existing `/Profile/Me/ShiftInfo`); `IShiftSignups`, `IShiftVolunteerProfiles`, `IShiftView` leave `ProfileController`; `AllShiftTags`/`EditableShiftTagIds` leave `ProfileViewModel` | none | Shifts, Users |
| 7 Email messaging | `SendMessage` GET/POST, view, `FacilitatedMessageRequestBuilder`, `TeamMessageOptionsProvider` → Email under `/Profile/{id}/SendMessage` (URL kept) | Email → Teams.Contracts (new leaf, no cycle) | Email, Users |
| 8 Gdpr deletion | `RequestDeletion` POST, `/User/Status`, `/User/Deletion`, `DownloadData` → Gdpr; deletion timestamps stay on `users` behind `IUserWrite`; `AccountDeletionService` stays in Users | Gdpr → Users.Contracts (has) | Gdpr, Users |
| 9 Consent privacy | `/Profile/Me/Privacy` page shell → Consent, composing the Gdpr deletion status and the legal documents the member has signed | Consent → Gdpr (has) | Consent, Users |
| 10 Cantina dietary | `cantina_dietary_profiles`, `ICantinaService`/`ICantinaWrite`, `/Profile/Me/Dietary` (Cantina controller, `/Profile` prefix), dietary things-to-do entry, `DietaryOptions`, migration `cantina.profile-dietary`; Shifts badge reads `ICantinaService`; Users keeps `MedicalConditions` on a new `/Profile/Me/Medical` form with its own things-to-do entry | Shifts → Cantina.Contracts (new leaf); Cantina → Shifts.Contracts only, no cycle | Cantina, Shifts, Users, Web |
| 11 Governance tier | `governance_membership_tiers` (userId, tier, grantedAt, expiresAt, grantedByApplicationId), `MembershipTier` → Governance.Contracts, tier badge VC → Governance, apply block leaves the Edit page (link to `/Governance/Applications/Create`), `SetMembershipTierAsync` + term expiry leave `IUserWrite`, migration `governance.profile-tier`; Onboarding, Workgroups, Tickets, Teams, Surveys, Agent re-point | all already → Governance.Contracts or add it (leaf) | Governance, Onboarding, Users, Web |
| 12 Consent review | `consent_reviews` (userId, status, at, byUserId, notes, rejectionReason, rejectedAt, rejectedBy, isApproved), `ConsentCheckStatus` → Consent contracts, `ApplyProfileOnboardingMutationAsync` splits: state transitions stay on `IUserWrite` (`SetSuspension`, `SetRejected`), review annotation goes to `IConsentWrite`; `NonCompliantMemberSuspension` + job stay in Users (they write `users.State`); migration `consent.profile-review` | Onboarding → Consent (has) | Consent, Onboarding, Users, Web |
| 13 Tickets participation | `tickets_event_participations`, enums → Tickets.Contracts, `ITicketsService.GetParticipations`/`GetOnsiteUsers`, `ITicketsWrite.Declare/Undo/SyncUpsert/SyncRemove/Backfill`, `IUserMerge` fold with the precedence rule, export slice, `/Profile/Me/DeclareNotAttending` + `UndoNotAttending` + no-show history + onsite chip as Tickets user parts, migration `tickets.event-participations`; Gate, Scanner, Shifts, Camps re-point | Gate/Scanner/Shifts/Camps already → Tickets.Contracts | Tickets, Gate, Scanner, Users, Web |
| 14 Email comm prefs | `email_communication_preferences`, `MessageCategory` → Email.Contracts (21-section `using` sweep), `ICommunicationPreferenceService`/`IUnsubscribeService`/token provider → Email, `/Unsubscribe/*`, `/Guest/*`, `/Profile/Me/CommunicationPreferences`, the panel VC and the admin card as an `AdminDetail` user part, migration `email.communication-preferences`; Notifications reads `InboxEnabled` via `IEmailService`; MailerLite sync re-points | Notifications → Email.Contracts (new; Email does not reference Notifications, no cycle) | Email, Notifications, MailerLite, Users, full suite |
| 15 GoogleIntegration identity | per §3.2: table, `IGoogleIntegrationService.GetWorkspaceIdentity`, `IGoogleIntegrationWrite.Set/Clear/SetStatusFromSync/ClearIfAddress`, Google Sync card + nobodies.team badge as GoogleIntegration user parts and VC, Set/Clear routes, migration `google.workspace-identities`; Users' delete/unlink paths call `ClearIfAddressAsync`; `EnsureGoogleInvariantAsync` and `TrySetGoogleEmailStatusFromSyncAsync` leave Users | Users → GoogleIntegration (has); Teams → GoogleIntegration.Contracts if needed (check Teams ↔ GoogleIntegration direction first; GoogleIntegration → Teams.Contracts today) | GoogleIntegration, Teams, Users, Web |
| 16 cleanup | remove readers of the three dead `users` columns; debt-ledger rows for every deferred drop; `Docs/Users.md` Part 2 rewritten to what is left; `docs/architecture/dependency-graph.md` and `design-rules.md` §8 ownership map; issue filed for the audience section; fresh reforge reading into `docs/reforge/` | — | full suite |

## 5. Running it

- **Fresh session, `/pd:orch`.** Main thread holds this file and dispatches one worker per commit,
  sequentially (each commit builds on the last). View moves (2–9) on sonnet-high; commit 0 and the data
  moves (1, 10–15) on opus-high; the ef-migration-reviewer runs inside every data commit before it is
  committed.
- **Branch:** one feature branch off `origin/main`; draft PR opened after commit 0
  ([`wip-prs-as-draft`](../../../memory/process/wip-prs-as-draft.md)), flipped to ready after commit 16 and
  the full suite. The preview deploy (`{pr_id}.n.burn.camp`) is where `/Debug/DataMigrations` is
  exercised once per migration and `/api/data-migrations` is checked.
- **Per-commit definition of done:** build clean; gates in the table green; the owner's `Docs/*.md`
  and `Docs/Users.md` updated; moved resx keys present in all six cultures; no new analyzer
  grandfathers; `git diff` of every touched `*ModelSnapshot.cs` reviewed; the commit's migration (if
  any) visible on the Debug screen with a non-zero pending count against a seeded dev DB.
- **Stop conditions** (worker reports, main thread decides, nothing is forced): a move needs a cycle
  that only a new `.Contracts` leaf can break, a hard rule fights the move, or a copy would need to
  read something not on `UserInfo`.
- **PR tending:** the builder tends its own PR ([`.claude/steward.md`](../../../.claude/steward.md));
  five review-round commits max.

## 6. Resolved with Peter

- Whole tables: new table + copy (§3.1), not table transfer.
- All copies on one screen with a public status endpoint, permanent home in Debug (§3.3); existing Temp
  screens fold into it. A migration reporting `done` on production is the approval for dropping what it replaced.
- Google address flags move (#15).
- Privacy page → Consent.
- Audience segmentation stays; issue to file.
- One PR. If the bots choke on the diff, the natural cut is after commit 9 (no migrations yet).
