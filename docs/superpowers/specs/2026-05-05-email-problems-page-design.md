# EmailProblems Admin Page — Design

**Issue:** [#660 — Add /Profile/Admin/EmailProblems page with consolidating merge action](https://github.com/nobodies-collective/Humans/issues/660)
**Date:** 2026-05-05
**Status:** Approved (brainstorm)

## Problem

`/Admin/DuplicateAccounts` predates the Profile/UserEmail rework and is confusing — it mixes `User.Email` (an Identity-internals copy) with the actual `UserEmail` rows. It only flags shared-email duplicates; many other UserEmail invariant violations sit silently in the database.

A new admin page surfaces every UserEmail invariant violation, scoped through the existing Profile-section infrastructure (no new direct database access).

The fundamental rule: **an email address belongs to exactly one user**, and each user has consistent `UserEmail` flags.

## Scope — cases scanned

1. User has multiple `IsPrimary` UserEmails
2. User has multiple `IsGoogle` UserEmails
3. User has zero `IsPrimary` UserEmails (with at least one verified row)
4. User has zero `IsGoogle` UserEmails
5. Two users sharing a UserEmail address (raw match OR normalization-equivalent — gmail/googlemail dot tricks)
6. Any unverified UserEmail (regardless of `IsPrimary`/`IsGoogle`)
7. Orphan UserEmail rows (UserId points to a tombstoned/anonymized user)
8. Users with `AspNetUserLogins` rows but zero UserEmail rows (ghost auth artifact — should be empty after issue #661 lands; scan to catch existing damage)

Empty result is the goal. Scan all users — missing a check hides problems.

## Architecture

### Routing & section ownership

- New page: `GET /Profile/Admin/EmailProblems` — `AdminOnly` policy. Profiles section.
- New controller: `Humans.Web/Controllers/ProfileAdminController.cs` (room to absorb other Profile-admin actions later; `ProfileBackfillAdminController` is **not** folded in this PR).
- Compare detail page: `GET /Profile/Admin/EmailProblems/Compare?userId1=...&userId2=...`
- Action endpoints (all AdminOnly, all `[ValidateAntiForgeryToken]`):
  - `POST /Profile/Admin/EmailProblems/Merge` (sourceUserId, targetUserId, notes)
  - `POST /Profile/Admin/EmailProblems/DeleteOrphanEmail` (emailId)
  - `POST /Profile/Admin/EmailProblems/DeleteGhostLogins` (userId)
- Existing `/Admin/DuplicateAccounts` page stays untouched until retired manually.

### Detection service — no direct DB access

- New service: `IEmailProblemsService` in `Humans.Application/Interfaces/Profiles/`, impl in `Humans.Application/Services/Profile/`.
- Single method: `Task<EmailProblemsReport> ScanAsync(CancellationToken ct = default)`.
- Consumes only existing section services — **never any `I*Repository` or `DbContext`**:
  - `IProfileService` — primary source via the `FullProfile` cache snapshot (existing pattern: `GetBirthdayProfilesFromSnapshot`, `SearchApprovedUsersFromSnapshot`).
  - `IUserEmailService` — for orphan detection (rows whose UserId is not in the FullProfile cache).
  - `IUserService` — for ghost-AspNetUserLogins detection (case 8).
- Live scan on every page load. At ~500 users, the FullProfile cache snapshot enumeration is trivial.

### Required additions to existing surfaces

- **`FullProfile` record** — extend to carry `IReadOnlyList<UserEmailSnapshot> UserEmails`, where `UserEmailSnapshot` is a small record `(Guid Id, string Email, bool IsVerified, bool IsPrimary, bool IsGoogle)`. The full rows are already loaded inside `FullProfile.Create` and discarded after deriving `PrimaryEmail`/`AllVerifiedEmails`/`GoogleEmail`; we retain them. Existing derived fields stay.
- **`IProfileService.GetFullProfileSnapshotAsync()`** → returns `_byUserId.Values` snapshot from `CachingProfileService`. Same pattern as existing snapshot-returning methods.
- **`IUserEmailService.GetOrphanUserEmailsAsync()`** → returns UserEmail rows whose UserId points to a non-existent or tombstoned User (`MergedToUserId is not null` OR User row absent). Implementation may use the FullProfile cache as the "live users" set. Service-layer scan, no new repo method.
- **`IUserService.GetUsersWithLoginsButNoEmailsAsync()`** → returns userIds for case 8.

### Merge kernel extraction

In `AccountMergeService`, refactor `AcceptAsync` to extract a private `FoldAsync` kernel:

```csharp
private async Task FoldAsync(
    Guid sourceUserId, Guid targetUserId,
    Guid adminUserId, AuditEntry audit,
    CancellationToken ct);
```

`FoldAsync` runs the `IUserMerge` fan-out (Teams, Roles, UserEmail-via-`ReassignToUserAsync`, Shifts, Notifications, etc.), tombstones source via `IUserService.AnonymizeForMergeAsync`, and writes the audit row — all inside one ambient `TransactionScope`. The audit entity type / id / description differ per caller (`AcceptAsync` audits against `AccountMergeRequest`; `AdminMergeAsync` audits against `User`), so the kernel takes a small `AuditEntry(AuditAction action, string entityType, Guid entityId, string description)` parameter rather than building the audit string itself.

Two entry points call it:

- **Existing `AcceptAsync(requestId, ...)`** — loads + validates the `AccountMergeRequest`, calls `FoldAsync`, then runs the request-specific `MarkVerifiedAsync(request.PendingEmailId)` step and updates request status. Signature unchanged.
- **NEW `AdminMergeAsync(sourceUserId, targetUserId, adminUserId, notes, ct)`** — pre-flight (source != target, both exist, neither already tombstoned), calls `FoldAsync`. No request row, no `MarkVerifiedAsync`. Notes prefixed "Admin-initiated via EmailProblems" so the audit trail is unambiguous.

UserEmail consolidation (collapse duplicate addresses, OR-combine `IsVerified`, clear `IsPrimary`/`IsGoogle` on incoming rows) already happens inside `IUserEmailRepository.ReassignToUserAsync`. No new merge-time logic needed for that.

`DuplicateAccountService.ResolveAsync` and `/Admin/DuplicateAccounts` are left untouched.

## Page UX

Hybrid layout (cross-user → single-user → system-level):

**Top: Cross-user conflicts (case 5)** — most critical
- One row per email collision: `email — User A ⇄ User B  [Compare]`
- "Compare" links to the Compare detail page.

**Middle: Single-user issues (cases 1, 2, 3, 4, 6)** — operator opens user's existing admin page
- One row per affected user, problems collapsed into a comma list.
- `User Name — multiple IsPrimary, 1 unverified  [Open emails ▸]`
- "Open emails" links to existing `/Profile/{id}/Admin/Emails`. No new fix UI on this page.

**Bottom: System-level issues (cases 7, 8)** — no live user, inline action
- `Orphan UserEmail "joe@old.com" (was userId xyz)  [Delete row ✕]`
- `Ghost AspNetUserLogins for userId xyz (3 rows)  [Delete logins ✕]`
- Each destructive action shows a confirm dialog before POST.

**Empty-state per section:** "No problems found" (proves we scanned).

**Header:** "Scanned at HH:MM:SS · N total problems".

### Compare page (`/Profile/Admin/EmailProblems/Compare`)

Side-by-side, two columns:
- Per side: display name, **complete UserEmail list** (every row, with `IsVerified`/`IsPrimary`/`IsGoogle` badges), team count, role count, last login, profile completeness — same data the existing `DuplicateAccountDetailViewModel` shows.
- Single action: "Merge: keep [User A] / [User B] as target" — radio + notes textbox + `[Merge]` button → confirm dialog → `POST /Profile/Admin/EmailProblems/Merge` → calls `AccountMergeService.AdminMergeAsync(sourceUserId, targetUserId, ...)`.
- **Never display `User.Email`** anywhere on the page.

## Authorization, audit, error handling

**Authorization:** `[Authorize(Policy = PolicyNames.AdminOnly)]` on the controller. All POST actions `[ValidateAntiForgeryToken]`.

**Audit:**
- `Merge` → existing `AuditAction.AccountMergeAccepted` written by `FoldAsync`, notes prefixed "Admin-initiated via EmailProblems".
- `DeleteOrphanEmail` → new `AuditAction.OrphanUserEmailDeleted`, entity `UserEmail`, description includes email + dangling userId.
- `DeleteGhostLogins` → new `AuditAction.GhostExternalLoginsDeleted`, entity `User`, description includes userId + count.

**Error handling:**
- `AdminMergeAsync` pre-flight throws `InvalidOperationException` for source==target, missing user, or already-tombstoned source. Controller catches, sets error toast, redirects.
- Orphan / ghost-logins delete: idempotent — already-gone returns success with "already cleaned up" info toast.
- Scan failures: bubble; standard error page. No partial-results UI.

**Concurrency:** Single admin operates this surface — no contention to design around.

## Testing

**Unit (`tests/Humans.Application.Tests/Services/Profile/EmailProblemsServiceTests.cs`)**
- One test per case (1–8): seed FullProfile cache snapshot + service stubs to produce the violation, assert the report contains exactly the expected entry.
- Empty snapshot → empty report.
- Normalization-equivalent collision test for case 5 (`joe@gmail.com` vs `j.oe@googlemail.com`).

**Unit (`tests/Humans.Application.Tests/Services/Profile/AccountMergeServiceTests.cs`)**
- Existing `AcceptAsync` tests stay green.
- `AdminMergeAsync` happy path: all `IUserMerge` impls invoked, source tombstoned, audit row written, no `MarkVerifiedAsync` call.
- `AdminMergeAsync` source==target → `InvalidOperationException`, no writes.
- `AdminMergeAsync` source already tombstoned → `InvalidOperationException`, no writes.

**Integration (`tests/Humans.Integration.Tests/AccountMerge/AdminMergeAsyncTests.cs`)**
- Full-fixture admin-initiated merge: two real users with overlapping UserEmails → exactly one row per email on target post-merge, source has zero UserEmail and zero AspNetUserLogins, target's pre-existing `IsPrimary`/`IsGoogle` selections preserved.

**Controller (`tests/Humans.Application.Tests/Controllers/ProfileAdminControllerTests.cs`)**
- AdminOnly policy enforcement.
- POST endpoints: missing antiforgery → reject; success → audit + redirect with toast.

**Architecture**
- Add a focused test asserting `EmailProblemsService` constructor only depends on `IProfileService`, `IUserEmailService`, `IUserService` (no `I*Repository` types). Locks in the "use existing infra, no exceptions" rule.
- Existing `ProfileArchitectureTests` already enforces no `Microsoft.EntityFrameworkCore` imports in services — automatic coverage for the new service.

## Out of scope

- Retiring `/Admin/DuplicateAccounts` (manual decision once new page surfaces zero issues).
- Folding `ProfileBackfillAdminController` into `ProfileAdminController`.
- Issue #661 (anonymization deletes AspNetUserLogins) — separate, shippable independently. Case 8 detection here serves as the safety net for existing damage.
- Per-case fix actions for cases 1, 2, 3, 4, 6 — handled by linking to existing `/Profile/{id}/Admin/Emails` surface, not by new UI.
