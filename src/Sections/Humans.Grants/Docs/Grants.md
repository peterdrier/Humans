<!-- freshness:triggers
  src/Sections/Humans.Grants/**
  tests/Humans.Grants.Tests/**
  src/Sections/Humans.LowIncome/Controllers/LowIncomeAdminController.cs
-->
<!-- freshness:flag-on-change
  Who may issue and revoke, the one-live-grant-per-person rule, expiry, the source write contract, and the notice paths.
-->

# Grants — Section Invariants

A grant entitles a human to a thing (a cheaper ticket, early entry for build, staying late for strike). How the thing is delivered is not this section's concern. Grants replaces Campaigns for 2027 onward (nobodies-collective/Humans#2099); Campaigns stays until every feature that used campaign codes has a replacement. Feature spec: [features/grants.md](features/grants.md).

Grants is the one record of who is entitled to what, for which event. Any part of the system that decides someone qualifies (Low Income today, others later) records it here through one narrow write; a person always knows what they hold and when it ends; the Board can see every grant and who gave or took it. Deliberate choices: state is computed from dates, never stored; a revoked grant keeps its row; a program is filled either by its managers or by one source section, never both. Out of scope: delivering the thing (codes, prices, gate lists), and per-person eligibility, which belongs to the deciding section.

## Concepts

- A **Grant program** is one kind of entitlement for one event (picked from the Settings events; the program's year follows it): a name, what it **entitles** the person to (shown to them verbatim), an optional default expiry, and **who issues** it: its own managers by hand, or a **source** section (Low Income) identified by a source key.
- A **Grant** entitles one person under one program. It is **Active** until it expires or is revoked; **Expired** the day after its expiry date (its own date, else the program's), read against today in Europe/Madrid; **Revoked** once revoked, whatever the date. Revoked wins over expired.
- A program is **Open** or **Closed**. Closed programs take no new grants; existing grants stay.

## Data Model

### GrantProgram

**Table:** `grant_programs`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| Name | string(200) | |
| Entitles | string(500) | What the person gets, in their notice and on their profile |
| Description | string(2000)? | Managers, Grants Admin and Board only |
| Year | int | the event's year, for display and sorting |
| EventSettingsId | Guid? | index; the program's event. A program without one keeps its stored `Year` label; no event is inferred |
| IssuedBy | GrantIssuer (string) | `Managers` or `Source` |
| SourceKey | string(32)? | Set only when `IssuedBy = Source`; the key the source section passes |
| GrantsExpireOn | LocalDate? | Default expiry for its grants; null = never |
| Status | GrantProgramStatus (string) | `Open` / `Closed` |
| CreatedByUserId, CreatedAt, UpdatedAt | | |

### GrantProgramManager

**Table:** `grant_program_managers` — PK (ProgramId, UserId), index UserId. Cascade-deleted with the program.

### Grant

**Table:** `grants`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| ProgramId | Guid | FK → grant_programs (Restrict) |
| UserId | Guid | The holder |
| SourceRef | string(128)? | The source section's own reference (e.g. its application id) |
| ExpiresOn | LocalDate? | Overrides the program's date |
| IssuedAt, IssuedByUserId | | |
| NotifiedAt | Instant? | Last time the notice went out |
| RevokedAt, RevokedByUserId, RevokeReason(500) | | Null while live |

**Indexes / constraints:** unique (ProgramId, UserId) filtered `RevokedAt IS NULL`: one live grant per person per program. Index UserId.

**Cross-section FKs:** none enforced; user ids are bare Guids (Users).

## Routing

Admin pages live at `/Grants/Admin` (`GrantsAdminController`), in the Admin sidebar as Grants → Programs. Members see their grants on their own profile (`/Profile/Me`, the "My grants" card); admins see a person's grants on `/Users/Admin/{id}`.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Grant holder | Sees their own grants on their profile; gets an email and in-app notice on issue, resend and revoke |
| Program manager | Sees the programs they manage; issues (managers' programs only), revokes, resends notices on them |
| Grants Admin, Admin | All manager capabilities on every program. Additionally creates and edits programs and names their managers |
| Board | Reads every program and its grants; changes nothing |
| UsersAdmin, Board, Admin | See a person's grants on the admin human-detail card (a program name links to its admin page only when the viewer may open it) |
| A source section (LowIncome) | Issues and revokes under the open program whose source key it passes, through `IGrantWrite` |

## Invariants

- The authorization handlers honour request cancellation; without an HTTP context they use `CancellationToken.None`.
- Admin program pages and edit forms render manager and actor ids through the Human component without profile links (its localized fallback for missing members), managers joined with ", " in stored order.
- Program creation and editing resolve posted managers to their surviving ids and deduplicate them before persistence and manager audits. A missing or tombstoned manager refuses the entire save before any program field or manager row changes.
- A person holds at most one live grant per program (filtered unique index); issuing to a holder is skipped (manual) or returns the existing id (source write). Issuance resolves merged recipient ids before deduplication and saves new grants under the survivor id. A live grant under any of the person's archived ids also counts as held, and source revocation finds it through any alias.
- Both issuance paths refuse missing or tombstoned recipients before persistence, audit or notices. A manual batch containing any unavailable recipient is refused in full; source issuance returns `GrantWriteRefusal.RecipientUnavailable`. Merge survivors remain eligible. Reads, export and revocation still find records stored under archived ids.
- A managers' program is filled only by hand; a source program only through `IGrantWrite` with the matching source key. A closed program takes no new grants either way.
- At most one program per source key is open at a time (refused on create and edit), so `IGrantWrite` callers pass only their key and Grants resolves the program. Source issue/revoke return `GrantIssueResult` / `GrantRevokeResult`: no open program and overlong source references/reasons are typed refusals before mutation, audit or notices. Issuing to a holder returns the existing grant id; revoking with no live grant succeeds with `Revoked = false`. Dependency failures propagate.
- A program's issuer and status must be defined enum values; anything else is refused on create and edit.
- Expiry is computed at read time from `ExpiresOn ?? program.GrantsExpireOn` against today in Europe/Madrid; no job flips state. The admin program view model orders grants by computed state, then newest issuance; service projections impose no grant display order.
- Manual revoke and resend require both program and grant IDs. The service refuses a missing grant or a grant outside that program before mutation, audit or notices; controllers translate that refusal to 404.
- Revoking keeps the row (who, when, why); a revoked grant cannot be resent or revoked again.
- Manual and source issuance share one persist/audit/notice pipeline. Persistence honours caller cancellation; after commit, audit and notices run with no cancellation. Batch and source issues write different audit descriptions.
- Every issue, revoke, resend and program change writes an audit entry (`GrantIssued`, `GrantRevoked`, `GrantNoticeResent`, `GrantProgram*`). Program edits persist fields and manager additions/removals together in one save; a failed manager write leaves the program unchanged and emits no success audit.
- Grant reads, GDPR exports (held grants and programs created), and issue/revoke/resend audits use the program's current Settings event year, falling back to its stored year when the event is unavailable. Audit year resolution finishes before the mutation commits.
- A failed email or in-app notice is logged and never fails the write; the two channels fail independently (an in-app outage still sends the email, and vice versa); a manager can resend. A successful recipient lookup supplies the email address too; if that lookup fails, the email step looks the person up again and uses that result's id, burner name, language and address.
- Admin pages and program-level refusals are English text, including for the program managers who reach them (`memory/code/localization-admin-exempt.md`; Peter, 2026-11-08). Member-facing copy (notice, email, My grants card) is in all supported cultures.

## Negative Access Rules

- A program manager **cannot** create programs, edit a program, or change its managers.
- A manager of one program **cannot** see or act on another program's grants (per-grant routes 404 when the grant is not in the routed program).
- Managers, Grants Admin and Admin **cannot** issue by hand under a source program. Another section **cannot** issue under a managers' program or under a source key it does not own.
- A grant holder **cannot** reach the admin pages, and **cannot** see anyone else's grants.
- Board **cannot** create, edit, issue, revoke or resend; it reads only.
- FinanceAdmin, TicketsAdmin and other roles **cannot** manage grants unless they are Grants Admin, Admin, or a named manager.

## Triggers

- When a grant is issued (by hand or source) or its notice resent: an email (`grant_issued` template, System category) in the person's language and an Informational `GrantReceived` notice linking to `/Profile/Me`; `NotifiedAt` is stamped.
- When a grant is revoked: an email (`grant_revoked`) with the reason if given, and an Informational `GrantRevoked` notice.
- When two accounts merge: grants and manager rows move to the surviving account; a duplicate live grant in the same program is revoked ("Duplicate after account merge") and audited. Duplicate revocation is saved before re-keying to respect the one-live-grant index, with both saves in one transaction: a failed re-key rolls back the revocation and emits no success audit.
- A sign-in cookie issued before a merge still carries the archived id; every authorization check and the "My grants" card resolve it through Users to the surviving account, so the person keeps their access and card. The program index and program page use the same `ProgramOperationRequirement.View` resource check, matching manager rows against every id the viewer has held, so programs authorized through an archived manager id remain discoverable.
- When a person is erased: their grants and manager rows are deleted; their id stays on programs created and grants issued or revoked (`GrantDecisions` slice).

## Cross-Section Dependencies

- **AuditLog**: `IAuditLogService.LogAsync`. Actions declared on `GrantsAuditActions` (`src/Sections/Humans.Grants/GrantsAuditActions.cs`), one `AuditActionDefinition` each: `GrantProgramCreated`, `GrantProgramEdited`, `GrantProgramManagerAdded`, `GrantProgramManagerRemoved`, `GrantIssued`, `GrantRevoked`, `GrantNoticeResent`.
- **Users**: `IUserService.GetUserInfoAsync` (merged ids, preferred language, burner names and canonical notice address). Implements `IUserMerge`.
- **Notifications**: `INotificationEmitter.SendAsync` (`GrantReceived`, `GrantRevoked`). Its sources are declared in `GrantsNotificationSources` (`src/Sections/Humans.Grants/GrantsNotificationSources.cs`), one `NotificationSourceDefinition` each: `GrantReceived` (informational, to the grant holder: a grant issued to the person, or its notice resent); `GrantRevoked` (informational, to the former holder: a grant the person held was revoked).
- **Email**: `IEmailService.SendAsync`; contributes `IEmailPreviewContributor` samples.
- **Settings**: `ISettingsService.GetAllEventSettingsAsync` (the events a program can be for, and naming them), `GetActiveEventSettingsAsync` (the new-program default).
- **LowIncome** (caller): issues and revokes through `IGrantWrite` with source key `low-income`.
- **Gdpr**: implements `IUserDataContributor` (slices `Grants`, `GrantDecisions`).
- Email and Tickets do not know Grants exists; delivery of the thing (e.g. a vendor code) is a later, separate concern.

## Architecture

Admin manager and batch-recipient lists share `Humans.initHumanSelectionList` in `site.js`: selected IDs are unique, removable hidden inputs; batch submit is enabled only for a nonempty list. The views initialize it in the layout's Scripts section after the shared helpers load.

The internal `GrantNotices` (not registered in DI) owns recipient lookup, the profile link and the complete issued/revoked email and in-app job, reused for resend. `GrantService` owns persistence, audit ordering and `NotifiedAt`.

**Owning services:** `GrantService`
**Owned tables:** `grant_programs`, `grant_program_managers`, `grants`
**Status:** (A) Migrated — born in its own project, 2026-10-21 (nobodies-collective/Humans#2099).

### Public contract

| Public interface | Methods | Notes |
|---|---:|---|
| `I<Section>Service` | — | None: no section reads grants |
| `IGrantWrite` | 2 | `IssueAsync(sourceKey, userId, sourceRef?, actor)` / `RevokeAsync(sourceKey, userId, reason?, actor)` for source sections (LowIncome today); a cross-section write is unavoidable because the source decides who qualifies |

- `GrantService` lives in `Services/` and never imports `Microsoft.EntityFrameworkCore`; `GrantRepository` (Singleton over `IDbContextFactory<GrantsDbContext>`) is the only path to the tables.
- No caching decorator: a handful of programs, read on a few admin pages and one profile card.
- Display stitching through `<vc:human>`.
