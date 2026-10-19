# Users decomposition — single-PR move plan

**Status:** draft for Peter's sign-off. **Scope:** one branch, one PR, one commit per move, run by one
orchestrating session. Nothing here drops a column or a table; every drop is a later, evidence-gated PR.

## 1. Why

`Humans.Users` (+ `.Contracts`) is 28,000 source lines (22,400 C#, 5,600 Razor; 8,400 lines of migrations
excluded) and 2,714 of the solution's 13,923 reforge points. About 7,000 of those lines are other sections'
data or screens that landed on `User`/`Profile` before sections could own person-keyed tables. The rest
(identity, addresses, merge, deletion, profile core, the `UserInfo` cache) is Users' own lane and stays.

Targets were settled with Peter on 2026-10-19 (chat). Corrections from the first draft: communication
preferences go to **Email** (not Notifications), **medical conditions stay in Users** (Art. 9 data, gated by
`MedicalDataViewer`), the privacy page goes to **Consent** (the legal-documents owner), deletion to **Gdpr**,
audience segmentation stays out of this PR.

## 2. The moves

Line counts are estimates from file sizes plus line ranges inside the shared files (`UserService`,
`ProfileController`, `Edit.cshtml`, `UserRepository`, `CachingUserService`, `AdminDetail.cshtml`).
"Kind" says what physically moves: **view** = screens and view models only, data stays in Users;
**columns** = fields on `profiles`/`users` become a new table in the target; **table** = a whole Users
table changes owner.

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
| 15 | Cleanup: dead readers, docs, ledger, reforge reading | Users | — | — | — | low |

Totals: ~6,200 source lines leave Users, ~4,700 test lines follow. Expected reforge: Users ~1,800
(from 2,714); Email, Tickets, Governance, Consent, Cantina each gain 100 to 400.

**Stays in Users, on purpose:** medical conditions (Art. 9; gets its own small `/Profile/Me/Medical`
form when the dietary page leaves), emergency contact, languages, burner CV, contact fields, profile
pictures, location, search, account merge, the Google address flags (`IsGoogle` invariant lives inside every
`UserEmailService` write; deferred), audience segmentation (belongs to a vendor-agnostic mailing-list
layer that does not exist yet; file an issue, see §6).

**Delete readers, keep columns:** `User.SuppressScheduleChangeEmails`, `User.UnsubscribedFromCampaigns`,
`User.GoogleEmailStatus` have no reader outside Users' own cache plumbing. Commit 15 removes the readers
and records each column in the debt ledger for an evidence-gated drop PR.

## 3. Mechanics every move shares

- **Reads** from the new owner go through its own `I<Section>Service` (cached, whole table in RAM via
  `TrackedCache`, like `CachingUserService`). The slice leaves `UserInfo`/`ProfileInfo`.
- **Writes** from other sections go through the owner's `I<Section>Write`; the matching method leaves
  `IUserWrite`/`IUserManagement`.
- **Screens** land in the owner's own controller under its own route, or stay on the Users page through
  a `UserPart` (`IUserPart` + `UserPartSlots.Profile` / `ProfileSidebar` / `AdminDetail` /
  `AdminDetailSidebar`; Tickets and Calendar already do this). The `/Profile/Me/*` prefix is allowed on
  another section's controller (Shifts' `ShiftProfileController` is the precedent) so member URLs can
  stay stable where that matters.
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
- **Migrations:** one generated migration per target context per commit, reviewed by the
  `ef-migration-reviewer` agent before commit
  ([`ef-migration-review-gate`](../../../memory/process/ef-migration-review-gate.md)); snapshot diffed
  after every EF tool run. No data SQL, ever
  ([`no-data-backfills`](../../../memory/process/no-data-backfills.md)).

### 3.1 Moving data: columns

For #1, #10, #11, #12 the data is columns on `profiles`. The rules give one shape:

1. Target gets its table (`finance_payment_details`, `cantina_dietary_profiles`, `governance_membership_tiers`,
   `consent_reviews`), keyed by `UserId`, no FK to `users`
   ([`no-cross-section-ef-joins`](../../../memory/architecture/no-cross-section-ef-joins.md)).
2. Writes go to the new table only. The legacy columns stop being written
   ([`no-column-drops-for-decoupling`](../../../memory/architecture/no-column-drops-for-decoupling.md)).
3. A **Temp admin copy screen** in the target (review count + sample → confirm → audit-logged POST),
   modelled on `/Profile/Admin/EmailProblems/BackfillLegacyEmails`, copies legacy → new once. It reads
   the legacy values through `IUserService` from fields kept on `ProfileInfo` and marked `[Obsolete]`
   for exactly that caller. The screen retires when its count reads zero in production, and the
   obsolete fields retire with it.
4. Until the operator has run the screen, the owner's read falls back to the obsolete field (same
   `[Obsolete]` suppression, same retirement). This keeps preview and QA correct without an operator step.
5. The column drop is a separate later PR per column set with the pickaxe evidence
   ([`no-drops-until-prod-verified`](../../../memory/architecture/no-drops-until-prod-verified.md)).

Enum pinch: `MembershipTier` and `ConsentCheckStatus` move to `Governance.Contracts` / Consent's
contracts. `Users.Contracts` cannot reference either (both already reference `Users.Contracts`), so the
obsolete legacy fields on `ProfileInfo` keep the **old** enum types, renamed `LegacyMembershipTier` /
`LegacyConsentCheckStatus`, and the copy screens map old → new. Both die with the screens.

### 3.2 Moving data: whole tables (#13, #14)

`event_participations` and `communication_preferences` are whole Users tables. Two options; **Peter picks**:

- **(A) Transfer the table in place** (recommended). The target context maps the existing table name;
  its generated `CreateTable` migration is hand-emptied to snapshot-only, and the Users context's
  generated `DropTable` migration is hand-emptied the same way. Zero data movement, no copy screen, no
  later drop PR. This is exactly what the per-section DbContext peel did (step 4 of
  `2026-07-15-per-section-dbcontext-design.md` §8), and it needs the same explicit authorization from
  Peter because [`no-hand-edited-migrations`](../../../memory/architecture/no-hand-edited-migrations.md)
  is a hard rule. On a fresh DB the Users chain's historical `CreateTable` still creates the table, so
  the target's empty migration is safe; startup order is already deterministic.
- **(B) New table + copy screen**, the §3.1 shape. Two more Temp screens, two later drop PRs, and for
  #14 the copy screen cannot read legacy rows through `UserInfo` without a `Users.Contracts →
  Email.Contracts` cycle, so Users would expose a one-off string-typed legacy read. Rule-clean but
  heavier.

Either way the enums move: `ParticipationStatus`/`ParticipationSource` → `Tickets.Contracts`,
`MessageCategory` → `Email.Contracts` (21 sections touch it; mechanical `using` sweep; no cycle since
`Email.Contracts` references only `Base` and `Users.Contracts`, and `Users.Contracts` drops its last use).

## 4. Commit order

Ordered so every commit builds green and the branch is shippable at any cut. Cheap view moves first
(they establish the slots the data moves reuse), then the data moves by rising blast radius, the enum
sweep last.

| Commit | Does | New references (cycle-checked) | Gate |
|---|---|---|---|
| 0 prep | `IAccountDeletionService` (and any `Humans.Users/Contracts/*` interface a target needs) moves from the `Contracts/` folder to the `Users.Contracts` project; `UserPartSlots` gains `ProfileEditSidebar` if the Edit page needs a slot | none | Users, Web |
| 1 Finance | table, `IFinanceWrite.SetIban`, `RevealIban` route under `/Finance/Admin/Members/{id}`, Payment Details as `AdminDetail` user part, export/erase/merge participants, copy screen, `SetProfileIbanAsync` leaves `IUserWrite`, Expenses/Holded read via `IFinanceService` | Expenses already → Finance.Contracts | Finance, Expenses, Users, Web |
| 2 Campaigns | "My codes" and "Campaign Codes" cards as `Profile`/`AdminDetail` user parts; `ICampaignService` leaves `ProfileController` and the admin builder | Campaigns → Users.Contracts (has) | Campaigns, Users |
| 3 Email outbox | `/Profile/Me/Outbox` and `/Users/Admin/{id}/Outbox` served by an Email controller; `IEmailOutboxService` leaves both Users controllers | Email → Users.Contracts (has) | Email, Users |
| 4 Auth roles | `/Users/Admin/Roles`, `AddRole`, `EndRole`, `_RolesListContent` move to Auth; role card becomes an `AdminDetail` user part | Auth → Users.Contracts (has) | Auth, Users |
| 5 Debug | `UsersAdminDebugController` + view + model → Debug | Debug → Users (has) | Debug, Users |
| 6 Shifts | shift-preference card leaves the Edit form (link to the existing `/Profile/Me/ShiftInfo`); `IShiftSignups`, `IShiftVolunteerProfiles`, `IShiftView` leave `ProfileController`; `AllShiftTags`/`EditableShiftTagIds` leave `ProfileViewModel` | none | Shifts, Users |
| 7 Email messaging | `SendMessage` GET/POST, view, `FacilitatedMessageRequestBuilder`, `TeamMessageOptionsProvider` → Email under `/Profile/{id}/SendMessage` (URL kept) | Email → Teams.Contracts (new leaf, no cycle) | Email, Users |
| 8 Gdpr deletion | `RequestDeletion` POST, `/User/Status`, `/User/Deletion`, `DownloadData` → Gdpr; deletion timestamps stay on `users` behind `IUserWrite`; `AccountDeletionService` stays in Users (orchestrator over Users tables) | Gdpr → Users.Contracts (has) | Gdpr, Users |
| 9 Consent privacy | `/Profile/Me/Privacy` page shell → Consent, composing the Gdpr deletion status and the legal documents the member has signed | Consent → Gdpr (has) | Consent, Users |
| 10 Cantina dietary | `cantina_dietary_profiles`, `ICantinaService`/`ICantinaWrite`, `/Profile/Me/Dietary` (Cantina controller, `/Profile` prefix), dietary things-to-do entry, `DietaryOptions`, copy screen; Shifts badge reads `ICantinaService`; Users keeps `MedicalConditions` on a new `/Profile/Me/Medical` form with its own things-to-do entry | Shifts → Cantina.Contracts (new leaf); Cantina → Shifts.Contracts only, no cycle | Cantina, Shifts, Users, Web |
| 11 Governance tier | `governance_membership_tiers` (userId, tier, grantedAt, expiresAt, grantedByApplicationId), `MembershipTier` enum → Governance.Contracts, tier badge VC → Governance, apply block leaves the Edit page (link to `/Governance/Applications/Create`), `SetMembershipTierAsync` + term expiry leave `IUserWrite`, copy screen; Onboarding, Workgroups, Tickets, Teams, Surveys, Agent re-point | all already → Governance.Contracts or add it (leaf) | Governance, Onboarding, Users, Web |
| 12 Consent review | `consent_reviews` (userId, status, at, byUserId, notes, rejectionReason, rejectedAt, rejectedBy, isApproved), `ConsentCheckStatus` → Consent contracts, `ApplyProfileOnboardingMutationAsync` splits: state transitions stay on `IUserWrite` (`SetSuspension`, `SetRejected`), review annotation goes to `IConsentWrite`; `NonCompliantMemberSuspension` + job stay in Users (they write `users.State`); copy screen | Onboarding → Consent (has) | Consent, Onboarding, Users, Web |
| 13 Tickets participation | per §3.2: table, enums → Tickets.Contracts, `ITicketsService.GetParticipations`/`GetOnsiteUsers`, `ITicketsWrite.Declare/Undo/SyncUpsert/SyncRemove/Backfill`, `IUserMerge` fold with the precedence rule, export slice, `/Profile/Me/DeclareNotAttending` + `UndoNotAttending` + no-show history + onsite chip as Tickets user parts; Gate, Scanner, Shifts, Camps re-point | Gate/Scanner/Shifts/Camps already → Tickets.Contracts | Tickets, Gate, Scanner, Users, Web |
| 14 Email comm prefs | per §3.2: table, `MessageCategory` → Email.Contracts (21-section `using` sweep), `ICommunicationPreferenceService`/`IUnsubscribeService`/token provider → Email, `/Unsubscribe/*`, `/Guest/*`, `/Profile/Me/CommunicationPreferences`, the panel VC and the admin card as an `AdminDetail` user part; Notifications reads `InboxEnabled` via `IEmailService`; MailerLite sync re-points | Notifications → Email.Contracts (new; Email does not reference Notifications, no cycle) | Email, Notifications, MailerLite, Users, full suite |
| 15 cleanup | remove readers of the three dead `users` columns; debt-ledger rows for every deferred drop; `Docs/Users.md` Part 2 rewritten to what is left; `docs/architecture/dependency-graph.md` and `design-rules.md` §8 ownership map; fresh reforge reading into `docs/reforge/` | — | full suite |

## 5. Running it

- **One session, `/pd:orch`.** Main thread holds this file and dispatches one worker per commit,
  sequentially (each commit builds on the last). View moves (2–7) on sonnet-high; data moves (1, 10–14)
  on opus-high; the ef-migration-reviewer runs inside every data commit before it is committed.
- **Branch:** one feature branch off `origin/main`; draft PR opened after commit 1
  ([`wip-prs-as-draft`](../../../memory/process/wip-prs-as-draft.md)), flipped to ready after commit 15 and
  the full suite. The preview deploy (`{pr_id}.n.burn.camp`) is where each copy screen is exercised once.
- **Per-commit definition of done:** build clean; gates in the table green; the owner's `Docs/*.md`
  and `Docs/Users.md` updated; moved resx keys present in all six cultures; no new analyzer
  grandfathers; `git diff` of every touched `*ModelSnapshot.cs` reviewed.
- **Stop conditions** (worker reports, main thread decides, nothing is forced): a move needs a cycle
  that only a new `.Contracts` leaf can break, a hard rule fights the move, or a copy screen would need
  to read something not on `UserInfo`.
- **Steward:** at ready-for-review the builder hands the PR to a thin steward session
  ([`steward-thin-session`](../../../memory/process/steward-thin-session.md)); five review-round commits max.

## 6. Needs Peter

1. **§3.2 (A) or (B)** for `event_participations` and `communication_preferences`. (A) needs one line
   from you authorizing the two snapshot-only migrations per table, as the peel got.
2. **Privacy page → Consent** is my reading of "legal"; there is no Legal section and Consent owns
   `legal_documents`. Say if you meant a new section.
3. **Audience segmentation** stays in Users for this PR; I will file an issue for a vendor-agnostic
   mailing-list section (over MailerLite today) that takes `UsersAudienceService` and the audience page
   with it. Say if you want it folded into Campaigns now instead.
4. **Google address flags** stay (your "good" read as agreement). Say if you meant move.
5. **One PR or two.** One PR as asked. If the bots choke on a ~10k-line diff, the natural cut is after
   commit 9 (view-only moves, no migrations) and 10–15 (the data moves). Your call, default is one.
