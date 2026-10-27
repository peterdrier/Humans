<!-- freshness:triggers
  src/Sections/Humans.Grants/**
-->
<!-- freshness:flag-on-change
  Who may issue and revoke, the one-live-grant-per-person rule, expiry, the source write contract, and the notice paths.
-->

# Grants — Section Invariants

A grant entitles a human to a thing (a cheaper ticket, early entry for build, staying late for strike). How the thing is delivered is not this section's concern. Grants replaces Campaigns for 2027 onward (nobodies-collective/Humans#2099); Campaigns stays until every feature that used campaign codes has a replacement. Feature spec: [features/grants.md](features/grants.md).

## Concepts

- A **Grant program** is one kind of entitlement for one year: a name, what it **entitles** the person to (shown to them verbatim), an optional default expiry, and **who issues** it: its own managers by hand, or a **source** section (Low Income) identified by a source key.
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
| Description | string(2000)? | Managers and Board only |
| Year | int | |
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
| Board, Admin | All manager capabilities on every program. Additionally creates and edits programs and names their managers |
| HumanAdmin, Board, Admin | See a person's grants on the admin human-detail card (a program name links to its admin page only when the viewer may open it) |
| A source section (LowIncome) | Issues and revokes under the open program whose source key it passes, through `IGrantWrite` |

## Invariants

- A person holds at most one live grant per program (filtered unique index); issuing to a holder is skipped (manual) or returns the existing id (source write).
- A managers' program is filled only by hand; a source program only through `IGrantWrite` with the matching source key. A closed program takes no new grants either way.
- At most one program per source key is open at a time (refused on create and edit), so `IGrantWrite` callers pass only their key and Grants resolves the program. Source issue/revoke return `GrantIssueResult` / `GrantRevokeResult`: no open program and overlong source references/reasons are typed refusals before mutation, audit or notices. Existing live grant IDs and the no-live-grant revoke outcome remain explicit. Dependency failures propagate.
- A program's issuer and status must be defined enum values; anything else is refused on create and edit.
- Expiry is computed at read time from `ExpiresOn ?? program.GrantsExpireOn` against today in Europe/Madrid; no job flips state.
- Revoking keeps the row (who, when, why); a revoked grant cannot be resent or revoked again.
- Every issue, revoke, resend and program change writes an audit entry (`GrantIssued`, `GrantRevoked`, `GrantNoticeResent`, `GrantProgram*`).
- A failed email or in-app notice is logged and never fails the write; the two channels fail independently (an in-app outage still sends the email, and vice versa); a manager can resend.
- Program-level refusals are English text: every page that shows them is admin-only (`memory/code/localization-admin-exempt.md`). Member-facing copy (notice, email, My grants card) is in all six cultures.

## Negative Access Rules

- A program manager **cannot** create programs, edit a program, or change its managers.
- A manager of one program **cannot** see or act on another program's grants (per-grant routes 404 when the grant is not in the routed program).
- Managers, Board and Admin **cannot** issue by hand under a source program. Another section **cannot** issue under a managers' program or under a source key it does not own.
- A grant holder **cannot** reach the admin pages, and **cannot** see anyone else's grants.
- FinanceAdmin, TicketAdmin and other roles **cannot** manage grants unless they are Board, Admin, or a named manager.

## Triggers

- When a grant is issued (by hand or source) or its notice resent: an email (`grant_issued` template, System category) in the person's language and an Informational `GrantReceived` notice linking to `/Profile/Me`; `NotifiedAt` is stamped.
- When a grant is revoked: an email (`grant_revoked`) with the reason if given, and an Informational `GrantRevoked` notice.
- When two accounts merge: grants and manager rows move to the surviving account; a duplicate live grant in the same program is revoked ("Duplicate after account merge") and audited.
- A sign-in cookie issued before a merge still carries the archived id; every authorization check and the "My grants" card resolve it through Users to the surviving account, so the person keeps their access and card.
- When a person is erased: their grants and manager rows are deleted; their id stays on programs created and grants issued or revoked (`GrantDecisions` slice).

## Cross-Section Dependencies

- **Users**: `IUserService.GetUserInfoAsync` / `GetUserInfosAsync` (merged ids, preferred language, burner names), `IUserEmailService.GetNotificationTargetEmailsAsync` (notice address). Implements `IUserMerge`.
- **Notifications**: `INotificationEmitter.SendAsync` (`GrantReceived`, `GrantRevoked`).
- **Email**: `IEmailService.SendAsync`; contributes `IEmailPreviewContributor` samples.
- **AuditLog**: `IAuditLogService.LogAsync`.
- **LowIncome** (caller): issues and revokes through `IGrantWrite` with source key `low-income`.
- **Gdpr**: implements `IUserDataContributor` (slices `Grants`, `GrantDecisions`).
- Email and Tickets do not know Grants exists; delivery of the thing (e.g. a vendor code) is a later, separate concern.

## Architecture

**Owning services:** `GrantService`
**Owned tables:** `grant_programs`, `grant_program_managers`, `grants`
**Status:** (A) Migrated — born in its own project, 2026-10-21 (nobodies-collective/Humans#2099).

### Public contract

| Public interface | Methods | Notes |
|---|---:|---|
| `IGrantService` | — | No cross-section reader yet |
| `IGrantWrite` | 2 | `IssueAsync(sourceKey, userId, sourceRef?, actor)` / `RevokeAsync(sourceKey, userId, reason?, actor)` for source sections (LowIncome today); a cross-section write is unavoidable because the source decides who qualifies |

- `GrantService` lives in `Services/` and never imports `Microsoft.EntityFrameworkCore`; `GrantRepository` (Singleton over `IDbContextFactory<GrantsDbContext>`) is the only path to the tables.
- No caching decorator: a handful of programs, read on a few admin pages and one profile card.
- Display stitching through `IUserService.GetUserInfosAsync` and `<vc:human>`.
