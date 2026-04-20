# Admin — Section Invariants

## Concepts

- **System administration** covers infrastructure-level operations: email outbox, background jobs, configuration, logs, and database health. Google integration tools have been consolidated into the Google section (`/Google`).
- **Human administration** covers person-level operations: viewing the humans list, managing role assignments, suspending/unsuspending, tier management, and account merging.
- **Purge** permanently deletes a human and all associated data. Only available in non-production environments.

## Actors & Roles

| Actor | Capabilities |
|-------|-------------|
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
- Duplicate account detection scans for email addresses appearing on multiple accounts (across User.Email and UserEmail.Email, with gmail/googlemail equivalence). Admin can resolve by archiving the duplicate and re-linking its logins to the real account.
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

- **Google Integration**: Sync settings, group management, and reconciliation are administered here.
- **Email**: Outbox pause/resume and retry/discard operations.
- **Legal & Consent**: Document version management is administered by Board and Admin.
- **Governance**: Role assignment management via human admin actions.
- **All sections**: Admin has override access to all areas of the system.

## Architecture — Current vs Target

See `docs/architecture/design-rules.md` for the full rules.

**Owning services:** `DuplicateAccountService`, `AccountMergeService`
**Owned tables:** Admin section is primarily an orchestrator — it calls other section services. `AccountMergeService` owns `account_merge_requests`.

## Target Architecture Direction

> **Status:** This section currently follows the "services in Infrastructure, direct DbContext" model. It will be migrated to the repository/store/decorator pattern per [`../architecture/design-rules.md`](../architecture/design-rules.md). **Delete this block once the migration lands and this section's services live in `Humans.Application` with `*Repository.cs` impls in `Humans.Infrastructure/Repositories/`.**

### Target repositories

- **`IAccountMergeRepository`** — owns `account_merge_requests`
  - Aggregate-local navs kept: `AccountMergeRequest` itself (scalar fields only)
  - Cross-domain navs stripped: `TargetUser`, `SourceUser`, `ResolvedByUser` (all → `User`, owned by Users/Identity). Replace with scalar `TargetUserId` / `SourceUserId` / `ResolvedByUserId` and stitch `User` in `AccountMergeService` via `IUserService`.
- **`DuplicateAccountService`** — no persistent state of its own; stateless detector that should be rewritten to consume `IUserService` / `IUserEmailService` / `IProfileService` rather than hitting `DbContext`. No new repository required for this service.

**Section-straddling note:** Both `DuplicateAccountService` and `AccountMergeService` are listed in §8 under Admin **and** Users/Identity. Neither owns `AspNetUsers`. A true `UserService` owning the user aggregate does not yet exist in the Users/Identity section, so these Admin-section services currently reach into `Users` directly. When Users/Identity grows a real `IUserService` + `IUserRepository`, the user-mutation steps in `AccountMergeService.AcceptAsync` / `DuplicateAccountService.ResolveAsync` (archive flag, login re-linking, role-assignment transfer) should move behind that service — `AccountMergeService` then orchestrates via `IUserService`, owning only the `account_merge_requests` lifecycle.

### Current violations

Observed in this section's service and controller code as of 2026-04-15:

- **Controller-layer violations (§2a — `AdminController` injects `HumansDbContext`):**
  - `Controllers/AdminController.cs:17,27,37` — `HumansDbContext` field + constructor injection (should be zero-DbContext in controllers).
  - `Controllers/AdminController.cs:168-169` — `_dbContext.Database.GetAppliedMigrationsAsync` / `GetPendingMigrationsAsync` in `DbVersion` action. Infrastructure concern; wrap behind a dedicated `IDatabaseHealthService` in Admin section.
  - `Controllers/AdminController.cs:234` — `_dbContext.Database.ExecuteSqlRawAsync("DELETE FROM hangfire.lock")` in `ClearHangfireLocks` action. Hangfire is infrastructure, not a domain table — wrap behind an `IHangfireMaintenanceService`, keep raw SQL inside it.
  - `Controllers/AdminController.cs:309-311` — `_dbContext.Users.Select(u => u.Id)` in `AudienceSegmentation` action. Table owned by **Users/Identity**. Call `IUserService.GetAllUserIdsAsync()`.
  - `Controllers/AdminController.cs:313-315` — `_dbContext.Profiles.Select(p => p.UserId)` in `AudienceSegmentation`. Table owned by **Profiles**. Call `IProfileService.GetAllProfileUserIdsAsync()`.
  - `Controllers/AdminController.cs:324-330, 344-348` — `_dbContext.TicketOrders` queries (with optional year filter) in `AudienceSegmentation`. Table owned by **Tickets**. Call `ITicketQueryService.GetMatchedOrderUserIdsAsync(year?)`.
  - `Controllers/AdminController.cs:332-338, 350-354` — `_dbContext.TicketAttendees` queries (with cross-domain `.TicketOrder.PurchasedAt` filter) in `AudienceSegmentation`. Table owned by **Tickets**. Call `ITicketQueryService.GetMatchedAttendeeUserIdsAsync(year?)`. The nested `a.TicketOrder.PurchasedAt` is an implicit cross-entity filter that also violates §6.
  - `Controllers/AdminController.cs:377-381` — `_dbContext.TicketOrders.Select(o => o.PurchasedAt)` in `AudienceSegmentation` (available-years dropdown). Owned by **Tickets**. Call `ITicketQueryService.GetAvailablePurchaseYearsAsync()`.
- **Cross-domain `.Include()` calls in services:**
  - `Infrastructure/Services/DuplicateAccountService.cs:185` — `.Include(u => u.RoleAssignments)` on `Users` query. Crosses **Users/Identity → Auth** (`role_assignments` owned by `RoleAssignmentService`). Strip `.Include`; fetch role assignments via `IRoleAssignmentService.GetAssignmentsForUserAsync(userId)` and stitch in memory.
  - `Infrastructure/Services/AccountMergeService.cs:46-47, 56-58, 67-68` — `.Include(r => r.TargetUser)` / `.SourceUser` / `.ResolvedByUser` on `AccountMergeRequests` queries. All three navs cross **Admin → Users/Identity** (`User` / `AspNetUsers`). Strip `.Include`s from the future `IAccountMergeRepository`; resolve user summaries via `IUserService.GetByIdsAsync(...)` after loading merge requests.
- **Cross-section direct DbContext reads in services:**
  - `Infrastructure/Services/DuplicateAccountService.cs:46, 184, 189` — `_dbContext.Users` reads (Users/Identity). See section-straddling note — target is `IUserService`.
  - `Infrastructure/Services/DuplicateAccountService.cs:53, 278` (read) — `_dbContext.UserEmails` (Profiles). Call `IUserEmailService`.
  - `Infrastructure/Services/DuplicateAccountService.cs:104, 371` — `_dbContext.Profiles` (Profiles). Call `IProfileService`.
  - `Infrastructure/Services/DuplicateAccountService.cs:396` (read) — `_dbContext.ContactFields` (Profiles). Call `IContactFieldService`.
  - `Infrastructure/Services/DuplicateAccountService.cs:401` (read) — `_dbContext.VolunteerHistoryEntries` (Profiles). CV entries are a sub-aggregate of Profile (PR #235); expose a read via `IProfileService` and call it here.
  - `Infrastructure/Services/DuplicateAccountService.cs:281, 399, 404` — `RemoveRange` on `UserEmails` / `ContactFields` / `VolunteerHistoryEntries`. Cross-section **writes** are worse than reads; these must move to reassignment/delete APIs on the owning Profiles services (e.g. `IUserEmailService.ReassignToUserAsync`, `IContactFieldService.ReassignToUserAsync`, `IProfileService.RemoveCVEntriesForUserAsync` or equivalent).
  - `Infrastructure/Services/AccountMergeService.cs:119, 126, 173` (reads) and `122, 178` (removes) — `_dbContext.UserEmails` (Profiles). Same target: `IUserEmailService` reassign / remove APIs.
  - `Infrastructure/Services/AccountMergeService.cs:221` — `_dbContext.Profiles` (Profiles). Call `IProfileService`.
  - `Infrastructure/Services/AccountMergeService.cs:247, 252` (reads) and `250, 255` (removes) — `ContactFields` / `VolunteerHistoryEntries` (Profiles). Same target as the DuplicateAccountService bullet above.
- **Within-section cross-service direct DbContext reads:** None found. (`AccountMergeService` is the only service reading `account_merge_requests`, and `DuplicateAccountService` does not touch it.)
- **Inline `IMemoryCache` usage in service methods:** None found in either service. Caching is not applied to this section today; `DuplicateAccountService` results are recomputed on demand and `account_merge_requests` is low-volume. No `Caching*` decorator is required until a measurable hot path appears.
- **Cross-domain nav properties on this section's entities:** `AccountMergeRequest.TargetUser`, `AccountMergeRequest.SourceUser`, `AccountMergeRequest.ResolvedByUser` — three navs into `User` (Users/Identity). See target-repositories section for the strip plan.

### Touch-and-clean guidance

Until this section is migrated end-to-end, when touching its code:

- **Do not add new `_dbContext.<OtherSectionTable>` reads or writes to `AdminController`.** If a new admin dashboard needs data from Users / Profiles / Tickets, add (or extend) a method on the owning section's service and call it from the controller. The existing `AudienceSegmentation` action (`AdminController.cs:305-397`) is the canonical example of what we are trying to stop doing.
- **When editing `DuplicateAccountService` or `AccountMergeService`**, treat every `_dbContext.UserEmails` / `Profiles` / `ContactFields` / `VolunteerHistoryEntries` hit as a migration opportunity: replace with the corresponding `IUserEmailService` / `IProfileService` / `IContactFieldService` call (CV / `VolunteerHistoryEntries` are sub-aggregate of Profile — add a remove-for-user method on `IProfileService` if one does not yet exist). `AccountMergeService.cs:119-128` and `DuplicateAccountService.cs:278-281` are the densest offenders and the best places to prove out the pattern.
- **When touching `AccountMergeService` load paths** (`AccountMergeService.cs:44-73`), strip the three `.Include(r => r.*User)` calls and resolve user summaries via `IUserService.GetByIdsAsync`. This is a prerequisite for the eventual `IAccountMergeRepository` extraction — the repository must return rows without cross-domain navs.
- **When touching `DuplicateAccountService.cs:184-189`**, drop `.Include(u => u.RoleAssignments)` and fetch role assignments through `IRoleAssignmentService`. Role-assignment transfer on merge should go through `IRoleAssignmentService` (or a future `IUserService`) rather than mutating nav collections.
- **Migration target for this section:** extract `IAccountMergeRepository` (owning `account_merge_requests`, scalar-only) into `Humans.Application/Interfaces/Repositories/`, move `AccountMergeService` into `Humans.Application/Services/`, and rewrite `DuplicateAccountService` as a stateless orchestrator over `IUserService` / `IUserEmailService` / `IProfileService` / `IRoleAssignmentService` with no `DbContext` dependency at all.
