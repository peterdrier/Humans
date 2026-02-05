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

### Issue 2: GDPR - Implement Right to be Forgotten (Data Deletion) :grey_question: NEEDS INPUT

**Status:** Not started - requires business decisions

**Decisions Needed:**
- Grace period before deletion (30 days suggested)
- What data to anonymize vs delete
- Admin workflow requirements

---

### Issue 3: GDPR - Implement Data Export (Data Portability) :grey_question: NEEDS INPUT

**Status:** Not started - requires business decisions

**Decisions Needed:**
- Export format (JSON, CSV, or both)
- Which fields to include
- Rate limiting for exports

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

### Issue 5: GDPR - Implement Cookie Consent :grey_question: NEEDS INPUT

**Status:** Not started - requires business decisions

**Decisions Needed:**
- Cookie categorization
- Consent banner design
- Cookie policy content

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

### Issue 11: GDPR - Define Data Retention Policies :grey_question: NEEDS INPUT

**Status:** Not started - requires legal/compliance decisions

**Decisions Needed:**
- Retention periods for each data type
- Archival vs deletion policy
- Compliance requirements

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

### Issue 18: GDPR - Expand Privacy Policy :grey_question: NEEDS INPUT

**Status:** Not started - requires legal content

**Decisions Needed:**
- Legal basis documentation
- Third-party processor list
- Spanish translation
- DPO contact information

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

### Issue 22: Performance - Add Pagination to List Views :x: TODO

**Status:** Not started

Lower priority - can be addressed when data volumes increase.

---

## Summary

| Priority | Total | Done | Todo | Needs Input |
|----------|-------|------|------|-------------|
| CRITICAL | 4 | 2 | 0 | 2 |
| HIGH | 6 | 4 | 0 | 2 |
| MEDIUM | 8 | 7 | 0 | 1 |
| LOW | 4 | 2 | 2 | 0 |
| **TOTAL** | **22** | **15** | **2** | **5** |

### Completed (15)
- #1 HTTP Security Headers
- #4 N+1 Query Fixes
- #6 Rate Limiting
- #7 DTO Validation
- #9 Database Indexes
- #10 ForwardedHeaders
- #12 AsNoTracking for Read-Only Queries
- #13 GetStatusBadgeClass Extraction
- #14 Constants for Magic Strings
- #15 CSRF on ExternalLogin
- #16 Status Code Pages
- #17 Response Compression
- #19 Re-consent Notifications
- #20 Null Reference Review (no change needed)

### Remaining Todo (2)
- #21 AllowedHosts (needs domain names)
- #22 Pagination (lower priority)

### Needs Business Input (5)
- #2 GDPR Data Deletion
- #3 GDPR Data Export
- #5 Cookie Consent
- #8 Caching Strategy
- #11 Data Retention Policies
- #18 Privacy Policy Content

---

*Last updated: 2026-02-05 by Claude (N+1 and AsNoTracking fixes)*
