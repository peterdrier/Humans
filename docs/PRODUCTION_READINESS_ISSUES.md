# Production Readiness Review - Issues Tracker

This document tracks production readiness issues identified during code review.

**Last Updated:** 2026-02-05

---

## Status Legend

| Status | Meaning |
|--------|---------|
| :white_check_mark: **DONE** | Implemented in commit `7206008` |
| :hourglass: **IN PROGRESS** | Partially complete or needs review |
| :x: **TODO** | Not started, needs implementation |
| :grey_question: **NEEDS INPUT** | Requires business/architectural decisions |

---

## CRITICAL Priority Issues

### Issue 1: Security - Add HTTP Security Headers Middleware :white_check_mark: DONE

**Status:** Implemented in `src/Profiles.Web/Program.cs`

Added middleware for:
- Content-Security-Policy (CSP)
- X-Frame-Options: DENY
- X-Content-Type-Options: nosniff
- Referrer-Policy: strict-origin-when-cross-origin
- Permissions-Policy

---

### Issue 2: GDPR - Implement Right to be Forgotten (Data Deletion) :white_check_mark: DONE

**Status:** Implemented

- 30-day grace period with cancellation option
- User can request deletion from Privacy & Data settings
- Account anonymization preserves audit trail (consent records, applications)
- Background job `ProcessAccountDeletionsJob` runs daily to process expired grace periods
- Email notifications for deletion requested and completed

Files modified:
- `User.cs` - Added `DeletionRequestedAt`, `DeletionScheduledFor` properties
- `ProfileController.cs` - Added `Privacy`, `RequestDeletion`, `CancelDeletion` actions
- `Views/Profile/Privacy.cshtml` - Privacy settings page
- `ProcessAccountDeletionsJob.cs` - Background job for deletion processing

---

### Issue 3: GDPR - Implement Data Export (Data Portability) :white_check_mark: DONE

**Status:** Implemented

- JSON export format with all user data
- Includes profile, contact fields, team memberships, role assignments, consent records, applications
- Accessible from Privacy & Data settings
- Export generates downloadable JSON file

Files modified:
- `ProfileController.cs` - Added `ExportData`, `DownloadData` actions
- `Views/Profile/Privacy.cshtml` - Export data UI

---

### Issue 4: Performance - Fix N+1 Query Problems :white_check_mark: DONE

**Status:** Implemented

Added batch query methods to eliminate N+1 patterns:
- `IConsentRecordRepository.GetConsentedVersionIdsByUsersAsync` - batch load consents for multiple users
- `IMembershipCalculator.GetUsersWithAllRequiredConsentsAsync` - batch check consent compliance
- `ITeamService.GetPendingRequestCountsByTeamIdsAsync` - batch load pending request counts

Updated callers:
- `SystemTeamSyncJob.SyncVolunteersTeamAsync` - uses batch consent check
- `MembershipCalculator.GetUsersRequiringStatusUpdateAsync` - uses batch consent check
- `SuspendNonCompliantMembersJob.ExecuteAsync` - batch loads users with profiles
- `TeamController.MyTeams` - uses batch pending request counts

---

## HIGH Priority Issues

### Issue 5: GDPR - Implement Cookie Consent :white_check_mark: DONE

**Status:** Implemented

- Simple banner for essential cookies only (no cookie categorization needed)
- Banner appears at bottom of page until dismissed
- Consent stored in cookie for 1 year
- Links to privacy policy for details

Files modified:
- `Views/Shared/_Layout.cshtml` - Cookie consent banner with JavaScript

---

### Issue 6: Security - Add Rate Limiting :white_check_mark: DONE

**Status:** Implemented in `src/Profiles.Web/Program.cs`

Added global rate limiter: 100 requests per minute per user/IP.

---

### Issue 7: Security - Add Input Validation Attributes to DTOs :white_check_mark: DONE

**Status:** Implemented

Updated DTOs with validation:
- `ApplicationSubmitRequest.cs` - Required, StringLength
- `ProfileUpdateRequest.cs` - Required, StringLength, Phone, RegularExpression
- `ConsentRequest.cs` - Required, StringLength

---

### Issue 8: Performance - Implement Caching Strategy :grey_question: NEEDS INPUT

**Status:** Not started - requires architectural decisions

**Decisions Needed:**
- Cache invalidation strategy
- TTL for different data types
- Distributed cache vs in-memory

---

### Issue 9: Performance - Add Missing Database Indexes :white_check_mark: DONE

**Status:** Implemented in migration `20260205013627_AddPerformanceIndexes`

Added indexes:
- `applications(UserId, Status)` - composite
- `consent_records(UserId, ExplicitConsent, ConsentedAt)` - composite
- `role_assignments(UserId, RoleName)` WHERE ValidTo IS NULL - partial

Note: `document_versions(LegalDocumentId)` and `team_join_requests(TeamId, Status)` already existed.

---

### Issue 10: Security - Add ForwardedHeaders Middleware :white_check_mark: DONE

**Status:** Implemented in `src/Profiles.Web/Program.cs`

Configured ForwardedHeaders for X-Forwarded-For and X-Forwarded-Proto.

---

## MEDIUM Priority Issues

### Issue 11: GDPR - Define Data Retention Policies :white_check_mark: DONE

**Status:** Implemented

Retention periods documented in Privacy Policy:
- Active accounts: Data retained while account is active
- Inactive accounts: 7 years for Spanish nonprofit legal compliance
- Consent records: Indefinite (GDPR audit trail requirement)
- Deleted accounts: 30-day grace period, then anonymized

Files modified:
- `Views/Home/Privacy.cshtml` - Data retention section added

---

### Issue 12: Performance - Add AsNoTracking to Read-Only Queries :white_check_mark: DONE

**Status:** Implemented

Added `AsNoTracking()` to read-only queries in:
- `TeamService.cs` - GetTeamBySlugAsync, GetTeamByIdAsync, GetAllTeamsAsync, GetUserCreatedTeamsAsync, GetUserTeamsAsync, GetPendingRequestsForApproverAsync, GetPendingRequestsForTeamAsync, GetUserPendingRequestAsync, GetTeamMembersAsync
- `MembershipCalculator.cs` - GetUsersRequiringStatusUpdateAsync
- `SystemTeamSyncJob.cs` - profile/member/role assignment queries
- `SuspendNonCompliantMembersJob.cs` - user batch load query

Note: `ContactFieldService.cs` and `ConsentRecordRepository.cs` already had proper AsNoTracking usage.

---

### Issue 13: Code Quality - Extract Duplicate GetStatusBadgeClass Method :white_check_mark: DONE

**Status:** Implemented

Created `src/Profiles.Web/Extensions/StatusBadgeExtensions.cs` with:
- `GetBadgeClass(this ApplicationStatus)` - extension method
- `GetApplicationStatusBadgeClass(string)` - for views
- `GetMembershipStatusBadgeClass(string)` - for membership status

Updated controllers and views to use shared extension.

---

### Issue 14: Code Quality - Replace Magic Strings and Numbers with Constants :white_check_mark: DONE

**Status:** Implemented

Created:
- `src/Profiles.Domain/Constants/RoleNames.cs` - Admin, Board, Metalead
- `src/Profiles.Application/Constants/PaginationDefaults.cs` - PageSize, MaxMotivationPreview, etc.

Updated `TeamService.cs` and `SystemTeamSyncJob.cs` to use constants.

---

### Issue 15: Security - Add CSRF Token to OAuth ExternalLogin Endpoint :white_check_mark: DONE

**Status:** Implemented in `src/Profiles.Web/Controllers/AccountController.cs`

Added `[ValidateAntiForgeryToken]` attribute to ExternalLogin.

---

### Issue 16: Error Handling - Add Status Code Pages Middleware :white_check_mark: DONE

**Status:** Implemented

- Added `UseStatusCodePagesWithReExecute` to Program.cs
- Updated `HomeController.Error` to handle status codes
- Created `Views/Home/Error404.cshtml`
- Updated `Views/Home/Error.cshtml`

---

### Issue 17: Performance - Add Response Compression :white_check_mark: DONE

**Status:** Implemented in `src/Profiles.Web/Program.cs`

Added Brotli and Gzip compression with `EnableForHttps = true`.

---

### Issue 18: GDPR - Expand Privacy Policy :white_check_mark: DONE

**Status:** Implemented

Comprehensive privacy policy added with:
- Data controller information (Nobodies Collective)
- DPO contact: admin@nobodies.es
- Legal basis for processing (Contract, Legitimate Interest, Legal Obligation, Consent)
- Data categories collected
- Data retention periods
- User rights (Access, Rectification, Erasure, Portability, Restriction, Objection)
- Cookie policy
- Third-party services (Google OAuth, Google Workspace)
- Complaint process (AEPD contact)

Note: Spanish translation not included - can be added as future enhancement.

Files modified:
- `Views/Home/Privacy.cshtml` - Complete rewrite with GDPR-compliant content

---

## LOW Priority Issues

### Issue 19: Code Quality - Complete TODO for Re-consent Notifications :white_check_mark: DONE

**Status:** Implemented in `src/Profiles.Infrastructure/Jobs/SyncLegalDocumentsJob.cs`

Added `SendReConsentNotificationsAsync` method that:
- Gets users requiring re-consent via `IMembershipCalculator`
- Sends `SendReConsentRequiredAsync` for each updated required document

---

### Issue 20: Code Quality - Fix Potential Null Reference Exceptions :white_check_mark: DONE (No Change Needed)

**Status:** Reviewed - no changes required

The `.First()` calls are safe in context:
- GroupBy guarantees non-empty groups
- View usage is guarded by condition checks
- Nested version access operates on related data that must exist

---

### Issue 21: Security - Restrict AllowedHosts Configuration :x: TODO

**Status:** Not started

Requires knowing production domain names.

---

### Issue 22: Performance - Add Pagination to List Views :white_check_mark: DONE

**Status:** Implemented

Added pagination (20 items/page) to:
- Admin/Applications (with status filter preservation)
- Admin/Teams
- Team/Index (12 items/page for card grid)
- TeamAdmin/Members
- TeamAdmin/Requests

All views use consistent pagination UI pattern with page number links.

---

## Service Implementations (Stubbed)

### Issue 23: Integration - Implement LegalDocumentSyncService :white_check_mark: DONE

**Status:** Implemented with GitHub API integration (Octokit)

Syncs legal documents from configurable GitHub repository:
- Fetches Spanish (canonical) and English (translation) versions
- Tracks commit SHAs to detect content changes
- Creates new `DocumentVersion` records when content updates
- Supports all document types: Statutes, PrivacyPolicy, TermsAndConditions, CodeOfConduct

**Configuration:** `appsettings.json` GitHub section with:
- Owner/Repository/Branch
- Optional AccessToken for private repos
- Document path mappings per type

**Files:**
- `Infrastructure/Configuration/GitHubSettings.cs`
- `Infrastructure/Services/LegalDocumentSyncService.cs`

---

### Issue 24: Integration - Implement Real EmailService :x: TODO

**Status:** Stubbed - logs to console instead of sending emails

**Priority:** MEDIUM - App runs but users won't receive notifications.

Current `StubEmailService` needs to be replaced with real email provider.

**11 email types need implementation:**
- Application submitted/approved/rejected
- Welcome email
- Re-consent required/reminder
- Access suspended
- Email verification
- Account deletion requested/completed

**Suggested providers:** SendGrid, Mailgun, or SMTP relay

---

### Issue 25: Integration - Implement Real GoogleSyncService :x: TODO

**Status:** Stubbed - logs to console instead of calling Google APIs

**Priority:** LOW - App runs but Google Drive provisioning won't work.

Current `StubGoogleSyncService` needs Google Workspace Admin SDK integration.

**Functions to implement:**
- Provision team/user Google Drive folders
- Sync resource permissions
- Add/remove users from team resources

**Requires:** Google Cloud service account with domain-wide delegation

---

## Summary

| Priority | Total | Done | Todo | Needs Input |
|----------|-------|------|------|-------------|
| CRITICAL | 5 | 5 | 0 | 0 |
| HIGH | 6 | 5 | 0 | 1 |
| MEDIUM | 9 | 8 | 1 | 0 |
| LOW | 5 | 3 | 2 | 0 |
| **TOTAL** | **25** | **21** | **3** | **1** |

### Completed (21)
- #1 HTTP Security Headers
- #2 GDPR Data Deletion (30-day grace period, anonymization)
- #3 GDPR Data Export (JSON format)
- #4 N+1 Query Fixes
- #5 Cookie Consent Banner
- #6 Rate Limiting
- #7 DTO Validation
- #9 Database Indexes
- #10 ForwardedHeaders
- #11 Data Retention Policies (7 years)
- #12 AsNoTracking for Read-Only Queries
- #13 GetStatusBadgeClass Extraction
- #14 Constants for Magic Strings
- #15 CSRF on ExternalLogin
- #16 Status Code Pages
- #17 Response Compression
- #18 Privacy Policy (comprehensive GDPR content)
- #19 Re-consent Notifications
- #20 Null Reference Review (no change needed)
- #22 Pagination for List Views
- #23 LegalDocumentSyncService (GitHub/Octokit integration)

### Remaining Todo (3)
- #21 AllowedHosts (needs production domain names)
- #24 EmailService (stubbed - logs only)
- #25 GoogleSyncService (stubbed - logs only)

### Needs Business Input (1)
- #8 Caching Strategy (optional - can implement later if needed)

---

*Last updated: 2026-02-05 by Claude (LegalDocumentSyncService implemented)*
