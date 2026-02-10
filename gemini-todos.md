# Gemini Todos: Profiles.net Pre-Release Audit Findings

This document outlines critical issues and recommendations identified during a comprehensive pre-release audit of the Profiles.net application. These items must be addressed before the application's release.

## Executive Summary

The audit is complete. While the application is using the modern .NET 10 stack, there are several critical configuration, privacy, and performance issues that make it unfit for production in its current state:
1.  **Build Configuration Issues:** Missing target framework declarations and suppressed security vulnerability warnings.
2.  **GDPR & Privacy Risks:** Missing personal data attributes and hidden administrative fields.
3.  **Severe Performance Bottlenecks:** Systemic N+1 query problems in background jobs and core services (Google Sync, Legal Documents, Contact Fields).
4.  **Reliability & UX Failures:** Lack of idempotency in external service integrations (Google Drive) and high risk of user spamming.

## Critical Issues and Action Items

### 1. Build Environment & Security Configuration

*   [x] **1.1 Fix Web Project Configuration:** Add `<TargetFramework>net10.0</TargetFramework>` to `src/Humans.Web/Humans.Web.csproj`. (CANCELLED - TargetFramework is correctly managed in Directory.Build.props)
*   [x] **1.2 Review and Resolve Suppressed Vulnerabilities:** Remove `NU1903` and `NU1902` from the `<NoWarn>` section in `Directory.Packages.props`. Investigate the actual vulnerabilities and update the affected packages instead of suppressing the warnings. (COMPLETED)

### 2. GDPR & Data Privacy Liabilities

*   [ ] **2.1 Address `AdminNotes` Field:** Remove or redesign the hidden `AdminNotes` field in `Profile.cs`. Under GDPR, users must have access to all data stored about them; a "hidden" notes field is a major compliance liability.
    *   **Files:** `src/Humans.Domain/Entities/Profile.cs`
*   [x] **2.2 Mark PII with `[PersonalData]`:** Add the `[PersonalData]` attribute to all PII fields to ensure they are handled correctly by the framework's identity and privacy features (e.g., during account deletion). (COMPLETED)
    *   **Affected Fields:** `FirstName`, `LastName`, `PhoneNumber`, `Latitude`, `Longitude`, `DisplayName`, `PreferredEmail`.

### 3. Critical Performance (N+1 Queries)

*   [x] **3.1 Fix N+1 in `ContactFieldService`:** Refactor `GetViewerAccessLevelAsync` to eliminate redundant database calls when checking permissions for lists of users. (COMPLETED - Implemented request-scoped caching for viewer data)
*   [x] **3.2 Fix N+1 in `SyncLegalDocumentsJob`:** Batch-load users in `SendReConsentNotificationsAsync`. (COMPLETED)
*   [ ] **3.3 Fix N+1 in `SendReConsentReminderJob`:** Batch-load users instead of fetching one-by-one in a loop.
*   [ ] **3.4 Fix N+1 in `GoogleWorkspaceSyncService`:** Refactor synchronization methods to use bulk fetching and pass already-loaded entities to helper methods.

### 4. Reliability, Idempotency & UX

*   [ ] **4.1 Fix Google Provisioning Idempotency:** Redesign `GoogleWorkspaceSyncService` to ensure resource creation in Google Drive is idempotent. The current implementation can create duplicate folders if the process is interrupted.
*   [ ] **4.2 Prevent User Spamming:**
    *   **4.2a** Add `LastConsentReminderSentAt` to track and limit reminder frequency.
    *   [x] **4.2b** Aggregate legal document update notifications into a single email summary. (COMPLETED)
*   [ ] **4.3 Refactor `SystemTeamSyncJob`:** Replace sequential blocking calls with batched/asynchronous processing.

### 5. Web Security

*   [x] **5.1 Fix XSS in Confirmation Dialogs:** Prevent XSS in team name confirmation dialogs by using `JavaScriptStringEncode`. (COMPLETED - Fixed in `Team/MyTeams.cshtml`)

### 6. General Optimization & Refactoring

*   [ ] **6.1 Optimize Controller Queries:** Refactor `AdminController` and `TeamController` to use projected queries and avoid redundant database lookups for membership status.
*   [ ] **6.2 Centralize Logic:** Move direct database logic from `AdminController` to service classes.
*   [ ] **6.3 Whole-DB Team Membership Caching:** Given the small user base (<500), implement a global `IMemoryCache` strategy for team memberships to eliminate all team-related database lookups during common operations. (LOW PRIORITY)