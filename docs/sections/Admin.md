# Admin — Section Invariants

System administration (outbox, config, logs, Hangfire) and human administration (suspend, tier, merge, purge). Primarily an orchestrator over other sections.

## Concepts

- **System administration** covers infrastructure-level operations: email outbox, background jobs, configuration, logs, and database health. Google integration tools have been consolidated into the Google section (`/Google`).
- **Human administration** covers person-level operations: viewing the humans list, managing role assignments, suspending/unsuspending, tier management, and account merging.
- **Purge** permanently deletes a human and all associated data. Only available in non-production environments.

## Data Model

### SystemSetting

Key/value store for runtime configuration flags. Per the Google Integration migration (PR #267), each consumer owns its own key-space via its own repository — there is no single shared `SystemSettings` repository that crosses key ownership.

**Table:** `system_settings`

| Key | Owning section | Purpose |
|-----|----------------|---------|
| `email_outbox_paused` | Email | When `"true"`, `ProcessEmailOutboxJob` skips processing |
| `DriveActivityMonitor:LastRunAt` | Google Integration | Last-run timestamp for drive-activity monitor |

| Property | Type | Purpose |
|----------|------|---------|
| Key | string | PK |
| Value | string | Setting value |

### AccountMergeRequest

Tracks pending and resolved merges between duplicate accounts.

**Table:** `account_merge_requests`

Cross-domain navs `TargetUser`, `SourceUser`, `ResolvedByUser` were stripped to FK-only when `AccountMergeService` / `DuplicateAccountService` moved into `Humans.Application.Services.Profile/` (the services now live alongside Profiles' other Application-layer services despite §8 listing them under Admin). User data resolves via `IUserService.GetByIdsAsync`.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Admin | Full system administration: email outbox (pause/resume/retry/discard), Hangfire dashboard, configuration review, in-memory logs, database version, clear Hangfire locks, email preview, purge humans. Google integration tools are consolidated at `/Google` |
| HumanAdmin, Board, Admin | Human administration: view humans list with admin detail, view/edit role assignments, manage tier, suspend/unsuspend, view audit log and email outbox per human |
| Board, Admin | Legal document management (create/edit documents and versions) |

## Invariants

- Admin can assign all roles (including Admin). Board and HumanAdmin can assign all roles except Admin.
- Configuration settings are auto-discovered via `ConfigurationRegistry` — any setting accessed through the `GetRequiredSetting`/`GetOptionalSetting` extension methods is automatically surfaced on the Configuration page. Settings are classified as critical (app won't function), recommended (feature degrades), or optional. Non-sensitive values display in full; sensitive values are masked.
- The email outbox can be paused and resumed. While paused, no outgoing emails are processed.
- Individual failed emails can be retried (re-queued) or discarded (permanently deleted).
- Sync settings control per-service Google sync behavior (None / AddOnly / AddAndRemove). Setting a service to None disables sync without redeploying.
- Purging a human permanently deletes the account and all associated data, including severing the OAuth link so the next Google login creates a fresh account.
- User merge consolidates two accounts into one, transferring all associated data to the surviving account.
- Duplicate account detection scans for email addresses appearing on multiple accounts (across `User.Email` and `UserEmail.Email`, with gmail/googlemail equivalence). Admin can resolve by archiving the duplicate and re-linking its logins to the real account.
- Hangfire locks can be cleared if background jobs are stuck; the application must be restarted afterward to re-register recurring jobs.

## Negative Access Rules

- HumanAdmin **cannot** access system administration (sync settings, email outbox, logs, Hangfire, configuration, Google group management, purge).
- Board **cannot** assign the Admin role.
- HumanAdmin **cannot** assign the Admin role.
- Board **cannot** purge humans, manage sync settings, manage the email outbox, or access the Hangfire dashboard.
- No one can purge their own account.
- Purge is disabled in production environments.

## Triggers

- When sync settings are changed, sync jobs respect the new mode on next execution.
- When the email outbox is paused, outgoing email processing stops until resumed.
- When a human is purged, all associated data is cascade-deleted.

## Cross-Section Dependencies

- **Google Integration:** `ISyncSettingsService` / `IGoogleSyncService` — sync settings, group management, reconciliation.
- **Email:** `IEmailOutboxService` — outbox pause/resume and retry/discard operations.
- **Legal & Consent:** `IAdminLegalDocumentService` — document version management by Board and Admin.
- **Governance:** `IRoleAssignmentService` — role assignment management via human admin actions.
- **Profiles:** `IProfileService`, `IUserService`, `IUserEmailService`, `IContactFieldService`, `IRoleAssignmentService` — `AccountMergeService` and `DuplicateAccountService` orchestrate across all of these.
- **All sections:** Admin has override access to all areas of the system.

## Architecture

**Owning services:** `AccountMergeService`, `DuplicateAccountService`
**Owned tables:** `account_merge_requests`
**Status:** (B) Partially migrated. `AccountMergeService` and `DuplicateAccountService` have moved into `Humans.Application/Services/Profile/` (alongside the Profiles section's other Application-layer services — despite design-rules §8 still listing them under Admin, they are service-ownership-neighboring Profiles in the code). **`AdminController` remains in `Humans.Web/Controllers`** with direct `HumansDbContext` injection, violating §2a.

### Target repositories

- **`IAccountMergeRepository`** — owns `account_merge_requests`
  - Aggregate-local navs kept: none (scalar fields only)
  - Cross-domain navs stripped: `TargetUser`, `SourceUser`, `ResolvedByUser` → `TargetUserId` / `SourceUserId` / `ResolvedByUserId`; stitch `User` data via `IUserService.GetByIdsAsync`.
- **`DuplicateAccountService`** — no persistent state; stateless orchestrator over `IUserService` / `IUserEmailService` / `IProfileService` / `IContactFieldService` / `IRoleAssignmentService`. No repository required.

**Section-straddling note:** Both `DuplicateAccountService` and `AccountMergeService` are listed in §8 under Admin **and** Users/Identity. Neither owns `AspNetUsers`. Their current location under `Humans.Application.Services.Profile/` reflects the fact that they orchestrate primarily over Profile-section state. When Users/Identity grows a real `IUserService` + `IUserRepository` with full mutation surface, the user-mutation steps in `AccountMergeService.AcceptAsync` / `DuplicateAccountService.ResolveAsync` (archive flag, login re-linking, role-assignment transfer) should move behind that service.

### Current violations

Baseline 2026-04-15; `AccountMergeService` / `DuplicateAccountService` migration updates inline.

- **Controller-layer violations (§2a — `AdminController` injects `HumansDbContext`):**
  - `Controllers/AdminController.cs:17, 27, 37` — `HumansDbContext` field + constructor injection (should be zero-DbContext in controllers).
  - `Controllers/AdminController.cs:168-169` — `_dbContext.Database.GetAppliedMigrationsAsync` / `GetPendingMigrationsAsync` in `DbVersion` action. Wrap behind a dedicated `IDatabaseHealthService`.
  - `Controllers/AdminController.cs:234` — `_dbContext.Database.ExecuteSqlRawAsync("DELETE FROM hangfire.lock")` in `ClearHangfireLocks` action. Wrap behind an `IHangfireMaintenanceService`.
  - `Controllers/AdminController.cs:309-311` — `_dbContext.Users.Select(u => u.Id)` in `AudienceSegmentation`. Owned by **Users/Identity**. Call `IUserService.GetAllUserIdsAsync()`.
  - `Controllers/AdminController.cs:313-315` — `_dbContext.Profiles.Select(p => p.UserId)` in `AudienceSegmentation`. Owned by **Profiles**. Call `IProfileService.GetAllProfileUserIdsAsync()`.
  - `Controllers/AdminController.cs:324-330, 344-348` — `_dbContext.TicketOrders` queries in `AudienceSegmentation`. Owned by **Tickets**. Call `ITicketQueryService.GetMatchedOrderUserIdsAsync(year?)`.
  - `Controllers/AdminController.cs:332-338, 350-354` — `_dbContext.TicketAttendees` queries with cross-domain `.TicketOrder.PurchasedAt` filter. Owned by **Tickets**. Call `ITicketQueryService.GetMatchedAttendeeUserIdsAsync(year?)`. Nested `a.TicketOrder.PurchasedAt` also violates §6.
  - `Controllers/AdminController.cs:377-381` — `_dbContext.TicketOrders.Select(o => o.PurchasedAt)` (available-years dropdown). Owned by **Tickets**. Call `ITicketQueryService.GetAvailablePurchaseYearsAsync()`.
- **Service-layer cross-domain `.Include()` calls:**
  - `DuplicateAccountService.cs:185` (post-move) — `.Include(u => u.RoleAssignments)` on `Users`. Crosses **Users/Identity → Auth**. Strip `.Include`; fetch via `IRoleAssignmentService.GetAssignmentsForUserAsync(userId)` and stitch in memory.
  - `AccountMergeService.cs:46-47, 56-58, 67-68` — `.Include(r => r.TargetUser)` / `.SourceUser` / `.ResolvedByUser` on `AccountMergeRequests`. All cross **Admin → Users/Identity**. Strip when `IAccountMergeRepository` is extracted; resolve via `IUserService.GetByIdsAsync`.
- **Cross-section direct DbContext reads in services:**
  - `DuplicateAccountService.cs:46, 184, 189` — `_dbContext.Users` reads (Users/Identity).
  - `DuplicateAccountService.cs:53, 278` — `_dbContext.UserEmails` (Profiles). Call `IUserEmailService`.
  - `DuplicateAccountService.cs:104, 371` — `_dbContext.Profiles` (Profiles). Call `IProfileService`.
  - `DuplicateAccountService.cs:396` — `_dbContext.ContactFields` (Profiles). Call `IContactFieldService`.
  - `DuplicateAccountService.cs:401` — `_dbContext.VolunteerHistoryEntries` (Profiles). CV entries are Profile's sub-aggregate (PR #235); expose a read via `IProfileService` and call it here.
  - `DuplicateAccountService.cs:281, 399, 404` — `RemoveRange` on `UserEmails` / `ContactFields` / `VolunteerHistoryEntries`. Cross-section **writes** are worse than reads; these must move to reassign/delete APIs on the owning Profiles services.
  - `AccountMergeService.cs:119, 126, 173` (reads), `:122, 178` (removes) — `_dbContext.UserEmails` (Profiles). Target: `IUserEmailService` reassign / remove APIs.
  - `AccountMergeService.cs:221` — `_dbContext.Profiles` (Profiles). Call `IProfileService`.
  - `AccountMergeService.cs:247, 252` (reads), `:250, 255` (removes) — `ContactFields` / `VolunteerHistoryEntries` (Profiles).
- **Within-section cross-service direct DbContext reads:** None found. `AccountMergeService` is the only service reading `account_merge_requests`; `DuplicateAccountService` does not touch it.
- **Inline `IMemoryCache` usage in service methods:** None. `DuplicateAccountService` recomputes on demand; `account_merge_requests` is low-volume. No decorator required until a measurable hot path appears.
- **Cross-domain nav properties on this section's entities:** `AccountMergeRequest.TargetUser`, `AccountMergeRequest.SourceUser`, `AccountMergeRequest.ResolvedByUser` — three navs into `User` (Users/Identity).

### Touch-and-clean guidance

- **Do not add new `_dbContext.<OtherSectionTable>` reads or writes to `AdminController`.** If a new admin dashboard needs data from Users / Profiles / Tickets, add (or extend) a method on the owning section's service and call it from the controller. The existing `AudienceSegmentation` action (`AdminController.cs:305-397`) is the canonical example of what we are trying to stop doing.
- **When editing `DuplicateAccountService` or `AccountMergeService`**, treat every `_dbContext.UserEmails` / `Profiles` / `ContactFields` / `VolunteerHistoryEntries` hit as a migration opportunity. `AccountMergeService.cs:119-128` and `DuplicateAccountService.cs:278-281` are the densest offenders and the best places to prove out the pattern.
- **When touching `AccountMergeService` load paths** (`:44-73`), strip the three `.Include(r => r.*User)` calls and resolve user summaries via `IUserService.GetByIdsAsync`. Prerequisite for the eventual `IAccountMergeRepository` extraction.
- **When touching `DuplicateAccountService.cs:184-189`**, drop `.Include(u => u.RoleAssignments)` and fetch role assignments through `IRoleAssignmentService`.
- **Migration target:** extract `IAccountMergeRepository` (owning `account_merge_requests`, scalar-only) into `Humans.Application/Interfaces/Repositories/`, and rewrite `DuplicateAccountService` as a stateless orchestrator over `IUserService` / `IUserEmailService` / `IProfileService` / `IRoleAssignmentService` with no `DbContext` dependency at all.
