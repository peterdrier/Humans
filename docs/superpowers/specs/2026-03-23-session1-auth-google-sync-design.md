# Session 1: Auth Fix + Google Sync Fixes

**Date:** 2026-03-23
**Issues:** #172, #193, #173, #195
**Migration required:** No

---

## Batch 1: #172 — Google Sign-In 500 on Missing Correlation Cookie

### Problem

Google OAuth sign-in returns a 500 error when the correlation cookie is missing or expired. The cookie validates that the OAuth callback corresponds to the session that initiated login. When absent (browser cookie settings, expired session, opened in new tab), the OAuth handler throws an unhandled `CorrelationException`.

**Root cause:** No `OnRemoteFailure` event handler is registered on the Google OAuth options in `Program.cs` (lines 124-134). The exception propagates through the middleware pipeline unhandled.

### Fix

Add an `OnRemoteFailure` handler to the Google OAuth configuration:

**File:** `src/Humans.Web/Program.cs`

- Register `Events.OnRemoteFailure` in the `AddGoogle()` options block
- Handler: log the error at Warning level, redirect to `/Account/Login?error=sign-in-failed`

**File:** `src/Humans.Web/Views/Account/Login.cshtml`

- Add error display markup that reads `Request.Query["error"]`
- Show a localized, dismissible alert for the `sign-in-failed` error code (e.g., "Sign-in failed. Please try again.")
- The login view currently has no error display — this is new scaffolding

### Scope

- 2 files changed
- No migration
- No new dependencies

---

## Batch 2: Google Sync Fixes (#193, #173, #195)

### #193 — Fix googlemail.com Handling

#### Problem

A previous fix (#139) canonicalized `@googlemail.com` → `@gmail.com` at storage time via `EmailNormalization.Canonicalize()` and a SQL migration. This mutated stored emails, causing:

1. **OAuth lookup failures** — Google returns the user's real email (e.g., `user@googlemail.com`) but the DB has `user@gmail.com`, so `GetExternalLoginInfoAsync` can't match.
2. **False sync drift** — `SyncGroupResourceAsync` compares stored emails (canonicalized) against Google API responses (real addresses), producing phantom "missing" and "extra" members.

#### Fix — Three Parts

**Part 1: Stop canonicalizing on storage**

Remove `EmailNormalization.Canonicalize()` calls from:
- `UserEmailService.cs` (lines ~100, ~336) — email creation/update paths
- `AccountController.cs` (line ~95) — OAuth callback path

Store whatever email address Google returns, preserving the user's real domain.

**Part 2: Normalize at comparison boundaries**

Add `EmailNormalization.NormalizeForComparison(string email)` that lowercases and maps `@googlemail.com` ↔ `@gmail.com` for comparison purposes only. Never persisted.

Update comparison sites to use it:
- `GoogleWorkspaceSyncService.SyncGroupResourceAsync` (lines ~814-894) — the HashSets that determine extra/missing members
- `GoogleWorkspaceSyncService` Drive sync comparisons (lines ~972-1000) — same HashSet pattern for Drive permission sync

**Part 3: Admin backfill with review**

New admin page or action that:
1. Queries Google Admin SDK Directory API for all domain users
2. Compares each user's Google-reported primary email against the stored email
3. Displays a diff table showing mismatches: "Stored: x@gmail.com → Google: x@googlemail.com"
4. Admin reviews and confirms before any writes are applied
5. Updates only confirmed mismatches — no blanket replacements

This respects the requirement for a human review step. The number of affected users is small and known (currently breaking in Groups).

**Files involved:**
- `src/Humans.Domain/Helpers/EmailNormalization.cs` — add `NormalizeForComparison()`, deprecate/remove `Canonicalize()`
- `src/Humans.Infrastructure/Services/UserEmailService.cs` — remove canonicalization calls
- `src/Humans.Infrastructure/Services/GoogleWorkspaceSyncService.cs` — use `NormalizeForComparison()` in sync comparisons
- `src/Humans.Web/Controllers/AdminController.cs` — add backfill review action
- `src/Humans.Web/Views/Admin/` — new backfill review view

---

### #173 — Duplicate Key Error on Google Group Sync

#### Problem

When editing a team's Google Group prefix, `EnsureTeamGroupAsync` (line ~1135 of `GoogleWorkspaceSyncService.cs`) creates a new `GoogleResource` without checking if an active record with the same `GoogleId` already exists. This violates the filtered unique index on `(TeamId, GoogleId) WHERE IsActive = true`.

#### Fix — Validation Before Linking

`EnsureTeamGroupAsync` currently returns `Task` (void). Change it to return a result object (e.g., `GroupLinkResult` with `Success`, `Warning`, `ErrorMessage` properties) so the controller can relay validation messages to the view.

Before linking a Google Group to a team, check for existing active `GoogleResource` records:

1. **Same group already active on this team:** Return error "This group is already linked to this team." Block the duplicate.
2. **Same group active on a different team:** Return error "This group is already linked to [Team Name]." Block the action — two teams should not share a Google Group.
3. **Same group exists but inactive on this team:** Return warning "This group was previously linked to this team. Reactivate it?" Require confirmation before reactivating.

Surface these as validation messages in the team edit UI, not raw database errors.

**Files involved:**
- `src/Humans.Infrastructure/Services/GoogleWorkspaceSyncService.cs` — add pre-check in `EnsureTeamGroupAsync`, change return type to result object
- `src/Humans.Web/Controllers/TeamController.cs` (lines ~657-717) — handle validation result, pass messages to view
- `src/Humans.Web/Views/Team/EditTeam.cshtml` — display validation warnings
- `src/Humans.Application/DTOs/` — add `GroupLinkResult` DTO

---

### #195 — Group Settings Remediation + Expanded Reference

#### Problem

The `/Admin/GroupSettingsResults` page detects Google Groups settings drift but:
1. Has no way to fix drifted settings (admin must fix manually in Google Admin console)
2. Shows only a subset of the 14 expected settings in the reference table
3. Has an incorrect expected value for `WhoCanViewMembership`

#### Fix — Three Parts

**Part 1: Add remediation capability**

- New method `RemediateGroupSettingsAsync(string googleGroupId)` on `GoogleWorkspaceSyncService`
- Applies all expected settings to the specified group via `GroupssettingsService`
- Respects SyncSettings mode (only runs if Google Groups sync is not `None`)
- Logs changes via audit trail
- New POST action `RemediateGroupSettings` on `AdminController`
- Per-group "Fix" button on the results page, plus "Fix All" for bulk remediation

**Part 2: Expand expected settings reference**

Update the reference table to show all 14 expected settings, including the currently hardcoded ones (`IsArchived`, `MembersCanPostAsTheGroup`, etc.).

**Part 3: Correct default value**

- `WhoCanViewMembership`: change from current value to `OWNERS_AND_MANAGERS`
- `IsArchived`: keep at `false` (current value is correct — `true` would make groups read-only/archived, preventing new posts)

Update consistently in all three locations: `BuildExpectedSettingsDictionary()`, `GoogleWorkspaceSettings.cs` default, and `appsettings.json`.

**Files involved:**
- `src/Humans.Infrastructure/Services/GoogleWorkspaceSyncService.cs` — add `RemediateGroupSettingsAsync`, update `BuildExpectedSettingsDictionary()`
- `src/Humans.Application/Interfaces/IGoogleSyncService.cs` — add interface method
- `src/Humans.Web/Controllers/AdminController.cs` — add POST action
- `src/Humans.Web/Views/Admin/GroupSettingsResults.cshtml` — add Fix buttons, expand reference table

---

## Cross-Cutting Notes

- **No EF Core migrations** — all changes are code/logic plus an API-driven backfill
- **Batch 1 and Batch 2 are independent** — can be developed in parallel worktrees
- **Within Batch 2**, #193/#173/#195 all touch `GoogleWorkspaceSyncService.cs` — should be one branch/PR to avoid conflicts
- **Testing:** Manual QA against Google Workspace staging. The backfill review (#193 Part 3) is inherently manual by design.
