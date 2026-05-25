<!-- freshness:triggers
  src/Humans.Application/Services/Legal/**
  src/Humans.Application/Services/Consent/**
  src/Humans.Domain/Entities/LegalDocument.cs
  src/Humans.Domain/Entities/DocumentVersion.cs
  src/Humans.Domain/Entities/ConsentRecord.cs
  src/Humans.Infrastructure/Data/Configurations/Legal/**
  src/Humans.Web/Controllers/LegalController.cs
  src/Humans.Web/Controllers/ConsentController.cs
  src/Humans.Web/Controllers/AdminLegalDocumentsController.cs
-->
<!-- freshness:flag-on-change
  ConsentRecord append-only DB-trigger invariant, document sync from GitHub, and the Consent Coordinator review queue (audit-only, NOT a gate for Volunteers admission) — review when Legal/Consent services/entities/controllers change.
-->

# Legal & Consent — Section Invariants

Legal documents synced from GitHub, per-version consent records (append-only), the Consent Coordinator audit/review queue.

## Concepts

- A **Legal Document** is a named, team-scoped document (e.g., "Privacy Policy", "Volunteer Agreement"). Documents on the Volunteers system team apply to every active human. Each document points at a folder in the configured GitHub repository and is synced from there by `LegalDocumentSyncService` / `SyncLegalDocumentsJob`.
- A **Document Version** is a specific revision of a legal document with an `EffectiveFrom` instant and a multi-language `Content` dictionary keyed by language code (Spanish `"es"` is canonical/legally binding). When the GitHub commit SHA for the canonical file changes, the sync produces a new version; if `RequiresReConsent` is true, affected users are re-notified.
- A **Consent Record** is an append-only audit entry linking a user to a specific document version with timestamp, IP, user-agent, content hash, and an `ExplicitConsent` flag. Consent records can never be updated or deleted — only new records can be inserted.
- **Consent Check** is an audit/annotation track maintained on the profile (`Profile.ConsentCheckStatus`). After a human signs all required documents, the status flips to `Pending` and the human appears in the Consent Coordinator review queue. CC actions (Clear / Flag / Reject) maintain the annotation but do NOT gate admission to the Volunteers team — admission is automatic once profile + consents are complete.
- The **Statutes** page (`/Legal`) is a separate, anonymous read of the association's statutes pulled directly from GitHub by `LegalDocumentService` (with in-memory caching) — it does not go through the `legal_documents` table.

## Data Model

### LegalDocument

**Table:** `legal_documents`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| Name | string (256) | |
| TeamId | Guid | FK → Teams section |
| GracePeriodDays | int | Default 7 |
| GitHubFolderPath | string? (512) | Folder path; sync discovers translations by naming convention |
| CurrentCommitSha | string (40) | |
| IsRequired | bool | Default true |
| IsActive | bool | Default true |
| CreatedAt | Instant | |
| LastSyncedAt | Instant | |

Aggregate-local nav `LegalDocument.Versions` kept. Cross-domain nav `LegalDocument.Team` is still declared on the entity (`LegalDocument.cs:30`); `LegalDocumentRepository.GetActiveRequiredDocumentsForTeamsAsync` still emits `.Include(d => d.Team)` (`LegalDocumentRepository.cs:101`) because `ConsentService.GetConsentDashboardAsync` reads `g.First().Team` to group dashboard rows by team. Strip is deferred until callers move to a stitched DTO.

**Cross-domain FK:** `TeamId` → Teams section. `Team.LegalDocuments` reverse nav also exists (`Team.cs:160`) — cross-domain collection on the Teams entity. Both sides are on borrowed time; the FK scalar is the canonical reference.

### DocumentVersion

**Table:** `document_versions`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| LegalDocumentId | Guid | FK → `legal_documents` |
| VersionNumber | string (50) | Display label |
| CommitSha | string (40) | |
| Content | jsonb | `Dictionary<string, string>` keyed by language code; `"es"` is canonical/legally binding |
| EffectiveFrom | Instant | |
| RequiresReConsent | bool | |
| CreatedAt | Instant | |
| ChangesSummary | string? (2000) | |

Aggregate-local nav `DocumentVersion.LegalDocument` kept. Aggregate-local nav `DocumentVersion.ConsentRecords` declared on the entity (`DocumentVersion.cs:65`) and configured in `DocumentVersionConfiguration.cs:46`; not currently walked by the service layer.

### ConsentRecord

Append-only per design-rules §12. **DB triggers** (`prevent_consent_record_update` / `prevent_consent_record_delete`, both calling `prevent_consent_record_modification()`) raise an exception on any UPDATE or DELETE against `consent_records`; only INSERT is allowed, to maintain GDPR audit-trail integrity. Architecture test `ConsentArchitectureTests.IConsentRepository_HasNoUpdateOrDeleteOrRemoveMethods` (`tests/Humans.Application.Tests/Architecture/ConsentArchitectureTests.cs`) pins the interface-level constraint.

**Table:** `consent_records`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| UserId | Guid | FK → Users section |
| DocumentVersionId | Guid | FK → `document_versions` |
| ConsentedAt | Instant | |
| IpAddress | string (45) | IPv6-capable; service passes value through unchanged |
| UserAgent | string (1024) | Service truncates to 500 chars before persisting |
| ContentHash | string (64) | SHA-256 hex of canonical Spanish content at consent time |
| ExplicitConsent | bool | Always true for valid records |

**Unique index:** `(UserId, DocumentVersionId)` — prevents duplicate consents for the same version.

Cross-domain nav `ConsentRecord.User` — still declared on the entity (`ConsentRecord.cs:24`); no current `.Include` walks it in the Application layer. Strip is a follow-up.
Cross-aggregate nav `ConsentRecord.DocumentVersion` — still declared (`ConsentRecord.cs:34`) and walked by `ConsentRepository.GetAllForUserAsync` / `GetAllForUserIdsAsync` (`.Include(c => c.DocumentVersion).ThenInclude(v => v.LegalDocument)`, `ConsentRepository.cs:95–96`, `ConsentRepository.cs:111–112`) to surface document name + version number on the user's consent-history view.

Per-user reads on `consent_records` chain-follow merge tombstones via `IUserService.GetMergedSourceIdsAsync(userId)` so consents signed under a now-merged source id surface for the fold target. Consent records stay at source after merge by design — DB triggers make any rewrite physically impossible, so `AnonymizeForMergeAsync` cannot move them.

## Routing

Three controllers serve this section.

| Controller | Route prefix | Auth |
|------------|-------------|------|
| `LegalController` | `/Legal/{slug?}` | `[AllowAnonymous]` — Statutes page only |
| `ConsentController` | `/Consent` (conventional) | `[Authorize]` |
| `AdminLegalDocumentsController` | `/Legal/Admin/Documents` | `[Authorize(Policy = PolicyNames.BoardOrAdmin)]` |

**Consent routes:**

| Route | Method | Action |
|-------|--------|--------|
| `/Consent` | GET | Dashboard (team-grouped document status) |
| `/Consent/Review?id={versionId}` | GET | Document review before consent |
| `/Consent/Submit` | POST | Record consent |

**Admin routes:**

| Route | Method | Action |
|-------|--------|--------|
| `/Legal/Admin/Documents` | GET | List all documents (optional `teamId` filter) |
| `/Legal/Admin/Documents/Create` | GET | Create form |
| `/Legal/Admin/Documents/Create` | POST | Create handler |
| `/Legal/Admin/Documents/{id}/Edit` | GET | Edit form (includes version list) |
| `/Legal/Admin/Documents/{id}/Edit` | POST | Update handler |
| `/Legal/Admin/Documents/{id}/Archive` | POST | Soft-delete (`IsActive = false`) |
| `/Legal/Admin/Documents/{id}/Sync` | POST | Trigger single-document sync |
| `/Legal/Admin/Documents/{id}/Versions/{versionId}/Summary` | POST | Edit version changes summary |

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Anyone (including anonymous) | View `/Legal` Statutes page |
| Any authenticated human | View own consent dashboard at `/Consent`. Sign or re-sign document versions. Accessible during onboarding (before becoming an active member) |
| ConsentCoordinator, VolunteerCoordinator, Board, Admin | Read access to the onboarding review queue at `/OnboardingReview` (`PolicyNames.ReviewQueueAccess`) |
| ConsentCoordinator, Board, Admin | Clear, Flag, or Reject consent checks (`PolicyNames.ConsentCoordinatorBoardOrAdmin`) |
| Board, Admin | Manage legal documents and document versions at `/Legal/Admin/Documents` (`PolicyNames.BoardOrAdmin`): create, edit, archive, trigger manual sync, edit version summaries |

## Invariants

- Consent records are immutable. Database triggers prevent UPDATE and DELETE operations on `consent_records`. Only INSERT is allowed to maintain GDPR audit trail integrity (§12). Architecture test: `ConsentArchitectureTests.IConsentRepository_HasNoUpdateOrDeleteOrRemoveMethods`.
- Legal documents can be global (required of all humans) or team-scoped (required when joining a specific team).
- When all required global documents have active consent, the human's consent check status transitions from unset to Pending.
- Legal documents are synced from a GitHub repository by a background job.
- When a new document version is published, existing consents for the old version become stale and re-consent is required.
- Per-user reads on `consent_records` chain-follow merge tombstones via `IUserService.GetMergedSourceIdsAsync(userId)` so consents signed under a now-merged source id surface for the fold target. Consent records stay at source after merge — DB triggers (`prevent_consent_record_update`, `prevent_consent_record_delete`) make any rewrite physically impossible.

## Negative Access Rules

- Regular humans **cannot** manage legal documents or document versions.
- ConsentCoordinator **cannot** manage legal documents or versions — they can only review and clear/flag consent checks.
- No one **can** update or delete consent records. They are permanently immutable.

## Triggers

- When a human signs all required global documents: their consent check status transitions to Pending AND `ISystemTeamSync.SyncVolunteersMembershipForUserAsync` admits them to the Volunteers system team. Admission does not depend on Consent Coordinator review.
- When a Consent Coordinator clears a consent check: `Profile.IsApproved` is set to true and `ConsentCheckStatus = Cleared`. This is an audit annotation; the human is already a Volunteer.
- When a Consent Coordinator flags a consent check: `Profile.IsApproved` is set to false, `ConsentCheckStatus = Flagged`, and `DeprovisionApprovalGatedSystemTeamsAsync` removes the user from Volunteers / Colaborador / Asociado teams. The Volunteers admission criteria explicitly exclude `ConsentCheckStatus == Flagged`, so the user stays out until Board or Admin resolves via `ProfileController.ApproveVolunteer`.
- When a new document version is published: affected humans are notified to re-consent. A background job sends re-consent reminders.
- A background job suspends humans who no longer have valid consents for required documents.

## Cross-Section Dependencies

- **Profiles:** `IProfileService` — consent-check status lives on the profile (read by `ConsentService` for the review-detail view); `IProfileService.GetActiveApprovedUserIdsAsync` is the fan-out target list when `LegalDocumentSyncService` notifies on a new published / re-consent-required version. `ConsentService` does **not** call into Profile or Onboarding directly after a consent submit — the threshold check (`OnboardingService.SetConsentCheckPendingIfEligibleAsync`) is invoked by the controller (`ConsentController.Submit`, `OnboardingWidgetController`) as a peer call alongside `ConsentService.SubmitConsentAsync`.
- **Teams:** `ITeamService` — `AdminLegalDocumentService` stitches team names in memory (replaces `.Include(d => d.Team)`); legal documents are team-scoped (Volunteers team = global).
- **Notifications:** `INotificationService` (in-app fan-out from `LegalDocumentSyncService`) and `INotificationInboxService.ResolveBySourceAsync` (auto-resolve `AccessSuspended` notifications from `ConsentService` once all required consents are complete).
- **Google Integration:** `ISystemTeamSync.SyncVolunteersMembershipForUserAsync` / `SyncCoordinatorsMembershipForUserAsync` — `ConsentService` re-syncs system team membership after each consent submit.
- **Governance:** `IMembershipCalculator.GetRequiredTeamIdsForUserAsync` / `HasAllRequiredConsentsAsync` — `ConsentService` resolves which teams' documents apply to a given user and whether all required consents are complete.
- **Users/Identity:** `IUserService.GetMergedSourceIdsAsync` — chain-follow merge tombstones on every per-user consent read so consents signed under a source id surface for the fold target. Consent records are immutable per §12 and stay at source.

`IGitHubLegalDocumentConnector` is owned by this section (interface in `Humans.Application.Interfaces.Legal`, implementation in `Humans.Infrastructure`); not a cross-section dependency.

## Architecture

**Owning services:** `LegalDocumentService`, `AdminLegalDocumentService`, `LegalDocumentSyncService` (document-side), `ConsentService` (consent-side) — all in `Humans.Application.Services.Legal` / `Humans.Application.Services.Consent`.
**Owned tables:** `legal_documents`, `document_versions`, `consent_records`
**Status:** (A) Migrated — all four owning services live in `Humans.Application`; all table access routes through owning-section repositories. One cross-domain nav strip deferred (`LegalDocument.Team` — details below).

- Services live in `Humans.Application.Services.Legal/` and `Humans.Application.Services.Consent/` and never import `Microsoft.EntityFrameworkCore`.
- `ILegalDocumentRepository` (impl `LegalDocumentRepository` in `Humans.Infrastructure/Repositories/Legal/`) is the only code path that touches `legal_documents` and `document_versions` via `DbContext`.
- `IConsentRepository` (impl `ConsentRepository` in `Humans.Infrastructure/Repositories/Consent/`) is the only code path that touches `consent_records` via `DbContext`. Exposes `AddAsync` and `GetXxxAsync` only — no `UpdateAsync`/`DeleteAsync`.
- **Decorator decision (T-04, 2026-05-16)** — Two-layer cache landed:
  - **Global** — `CachingLegalDocumentSyncService` (Singleton in Infrastructure) wraps `LegalDocumentSyncService` (inner, keyed Scoped). Holds the active+required document set as `LegalDocumentInfo[]` keyed by document id, plus a version-id → document-id index. Serves `GetActiveRequiredDocumentsForTeamsAsync`, `GetRequiredDocumentVersionsForTeamAsync`, `GetRequiredVersionsAsync`, `GetVersionByIdAsync` from cache. `LegalDocumentSaveChangesInterceptor` uses the dual-override pattern (snapshot affected entity state in `SavingChangesAsync`, consume in `SavedChangesAsync`, clear on failure) to flush the cache wholesale after any persisted write to `legal_documents` or `document_versions`. Eager warmup at startup: the decorator inherits `TrackedCache<Guid, LegalDocumentInfo>` with `warmOnStartup: true`, so its own `IHostedService.StartAsync` drives `WarmAllAsync` at boot (non-fatal); no separate warmup hosted service. Team names are stitched at warm time via `ITeamService.GetByIdsWithParentsAsync` so the cache build no longer walks the deprecated `LegalDocument.Team` cross-domain nav.
  - **Per-user** — `CachingConsentService` (Singleton in Infrastructure) wraps `ConsentService` (inner, keyed Scoped). Holds `UserConsentInfo` (the user's explicitly consented version-id set, with the merge source-id chain unioned at warm time) keyed by user id. Serves `GetConsentedVersionIdsAsync`, `GetConsentMapForUsersAsync`, and `GetRequiredConsentRowsForUserAsync` from cache. Lazy per-user warm. **Synchronous invalidation on `SubmitConsentAsync`**: the decorator override evicts the user (and any merged-source-id tombstones) inline before returning, so the controller's next-page consent-banner check observes the fresh state. `AccountMergeService.FoldAsync` evicts both source and target post-commit so the surviving target's cached set rebuilds against the new chain.
  - `LegalDocumentService` (Statutes page) keeps its own `IMemoryCache` for the GitHub-fetched anonymous statutes content (zero DB access, pure I/O cache; out of scope for T-04).
  - Architecture tests pin both the inner-service IMemoryCache-free constraint and the decorator's dual-interface shape: `ConsentArchitectureTests.{ConsentService_HasNoIMemoryCacheConstructorParameter, LegalDocumentSyncService_HasNoIMemoryCacheConstructorParameter, CachingConsentService_ImplementsBothServiceAndInvalidator, CachingLegalDocumentSyncService_ImplementsBothServiceAndInvalidator, CachingConsentService_DeclaresSubmitConsentAsync}`.
- **Read/write interface split.** `IConsentServiceRead` (6 methods: `GetConsentedVersionIdsAsync`, `GetConsentMapForUsersAsync`, `GetRequiredConsentRowsForUserAsync`, `GetPendingDocumentNamesAsync`, `GetConsentRecordCountAsync`, `GetConsentReviewDetailAsync`) is the cross-section read surface — only Consent projections, no EF entities. `IConsentService : IConsentServiceRead` adds `SubmitConsentAsync` (write), `GetConsentDashboardAsync` (section-internal read), and the obsolete `GetUserConsentRecordsAsync`. External read-only consumers (MembershipCalculator, OnboardingWidgetState, AgentUserSnapshotProvider, ProfileController) inject `IConsentServiceRead`; OnboardingWidgetController stays on `IConsentService` (writes). See `memory/architecture/section-read-write-split.md`.
- **Cross-domain navs still declared (strip deferred):**
  - `LegalDocument.Team` (`LegalDocument.cs:30`) — walked by `LegalDocumentRepository.GetActiveRequiredDocumentsForTeamsAsync` (`.Include(d => d.Team)`, `LegalDocumentRepository.cs:101`) because `ConsentService.GetConsentDashboardAsync` groups by `g.First().Team`. Strip requires moving to a stitched DTO via `ITeamService`.
  - `Team.LegalDocuments` (`Team.cs:160`) — reverse collection on the Teams entity; not walked by this section. Strip follows the `LegalDocument.Team` strip.
  - `ConsentRecord.User` (`ConsentRecord.cs:24`) — declared but no current `.Include` walks it in the Application layer.
  - `ConsentRecord.DocumentVersion` (`ConsentRecord.cs:34`) — walked by `ConsentRepository.GetAllForUserAsync` / `GetAllForUserIdsAsync` (`.ThenInclude(v => v.LegalDocument)`, `ConsentRepository.cs:95–96`, `111–112`) to surface document name + version on the consent-history view. This is aggregate-local for `consent_records` → `document_versions` → `legal_documents`; not a cross-section nav.
  - `DocumentVersion.ConsentRecords` (`DocumentVersion.cs:65`) — declared and configured (`DocumentVersionConfiguration.cs:46`); not navigated by any current service path.
- **Cross-section calls:** `IProfileService`, `IOnboardingService`, `ITeamService`, `INotificationService`, `INotificationInboxService`, `ISystemTeamSync`, `IMembershipCalculator`, `IUserService`.
- **Architecture tests:** `tests/Humans.Application.Tests/Architecture/LegalArchitectureTests.cs` (Legal services, repository, connector), `tests/Humans.Application.Tests/Architecture/ConsentArchitectureTests.cs` (ConsentService, IConsentRepository append-only shape).

### Touch-and-clean guidance

- `LegalDocumentRepository.cs:101` — `.Include(d => d.Team)` inside `GetActiveRequiredDocumentsForTeamsAsync`. T-04 sidesteps this path on the hot read (the cache stitches team names via `ITeamService` at warm time), but `LegalDocumentSyncService.GetActiveRequiredDocumentsForTeamsAsync` still feeds the cache miss / fallback path and the `ConsentService.GetConsentDashboardAsync` dashboard view, both of which still read `Team.Name` off the included nav. Strip is still required to drop `LegalDocument.Team`, `Team.LegalDocuments`, and the Include — stitch via `ITeamService.GetTeamNamesByIdsAsync` in the remaining callers.
- `ConsentRecord.User` (`ConsentRecord.cs:24`) — declared but unused. Can be stripped in isolation; no callers need updating.
- `DocumentVersion.ConsentRecords` (`DocumentVersion.cs:65`) — declared but not navigated by any service. Can be stripped once confirmed no callers in views or tests depend on it.
- `ConsentArchitectureTests.cs` summary comment (lines 28–32) says Legal services "remain in Infrastructure" — this is stale; all four services are in Application post-migration. Update or remove when next touching that file.
