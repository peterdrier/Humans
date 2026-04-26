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
  ConsentRecord append-only DB-trigger invariant, document sync from GitHub, and consent-coordinator review gate — review when Legal/Consent services/entities/controllers change.
-->

# Legal & Consent — Section Invariants

Legal documents synced from GitHub, per-version consent records (append-only), the Consent Coordinator review gate.

## Concepts

- A **Legal Document** is a named, team-scoped document (e.g., "Privacy Policy", "Volunteer Agreement"). Documents on the Volunteers system team apply to every active human. Each document points at a folder in the configured GitHub repository and is synced from there by `LegalDocumentSyncService` / `SyncLegalDocumentsJob`.
- A **Document Version** is a specific revision of a legal document with an `EffectiveFrom` instant and a multi-language `Content` dictionary keyed by language code (Spanish `"es"` is canonical/legally binding). When the GitHub commit SHA for the canonical file changes, the sync produces a new version; if `RequiresReConsent` is true, affected users are re-notified.
- A **Consent Record** is an append-only audit entry linking a user to a specific document version with timestamp, IP, user-agent, content hash, and an `ExplicitConsent` flag. Consent records can never be updated or deleted — only new records can be inserted.
- **Consent Check** is the safety gate in the onboarding pipeline. After a human signs all required documents, a Consent Coordinator reviews and either clears or flags the check.
- The **Statutes** page (`/Legal`) is a separate, anonymous read of the association's statutes pulled directly from GitHub by `LegalDocumentService` (with in-memory caching) — it does not go through the `legal_documents` table.

## Data Model

### LegalDocument

**Table:** `legal_documents`

Team-scoped (documents on the Volunteers system team are effectively global). Fields: `Id`, `Name`, `TeamId`, `GracePeriodDays` (default 7), `GitHubFolderPath`, `CurrentCommitSha`, `IsRequired` (default true), `IsActive` (default true), `CreatedAt`, `LastSyncedAt`. Aggregate-local nav `LegalDocument.Versions` kept. Cross-domain nav `LegalDocument.Team` is **still declared** on the entity for read-side stitching (`ConsentService` reads `g.First().Team` when grouping the consent dashboard); strip is deferred until callers move to a stitched DTO.

### DocumentVersion

**Table:** `document_versions`

Fields: `Id`, `LegalDocumentId`, `VersionNumber`, `CommitSha`, `Content` (jsonb dictionary keyed by language code; Spanish `"es"` is canonical/legally binding), `EffectiveFrom`, `RequiresReConsent`, `CreatedAt`, `ChangesSummary`. Aggregate-local nav `DocumentVersion.LegalDocument` kept.

### ConsentRecord

Append-only per design-rules §12. **DB triggers** (`prevent_consent_record_update` / `prevent_consent_record_delete`, both calling `prevent_consent_record_modification()`) raise an exception on any UPDATE or DELETE against `consent_records`; only INSERT is allowed, to maintain GDPR audit-trail integrity.

**Table:** `consent_records`

Fields: `Id`, `UserId`, `DocumentVersionId`, `ConsentedAt`, `IpAddress` (max 45 chars, IPv6), `UserAgent` (max 1024 chars; service truncates to 500 before persisting), `ContentHash` (SHA-256 hex of the canonical Spanish content at consent time), `ExplicitConsent`. Unique index on `(UserId, DocumentVersionId)` prevents duplicate consents for the same version.

Cross-domain nav `ConsentRecord.User` — still declared on the entity but no longer navigated by `ConsentService`; strip is a follow-up.
Cross-aggregate nav `ConsentRecord.DocumentVersion` — still declared and walked by `ConsentRepository.GetAllForUserAsync` (the only remaining `.Include` on this side, scoped to the consent-history read).

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Anyone (including anonymous) | View `/Legal` Statutes page |
| Any authenticated human | View own consent dashboard at `/Consent`. Sign or re-sign document versions. Accessible during onboarding (before becoming an active member) |
| ConsentCoordinator, VolunteerCoordinator, Board, Admin | Read access to the onboarding review queue at `/OnboardingReview` (`PolicyNames.ReviewQueueAccess`) |
| ConsentCoordinator, Board, Admin | Clear, Flag, or Reject consent checks (`PolicyNames.ConsentCoordinatorBoardOrAdmin`) |
| Board, Admin | Manage legal documents and document versions at `/Admin/LegalDocuments` (`PolicyNames.BoardOrAdmin`): create, edit, archive, trigger manual sync, edit version summaries |

## Invariants

- Consent records are immutable. Database triggers prevent UPDATE and DELETE operations on consent records. Only INSERT is allowed to maintain GDPR audit trail integrity (§12).
- Legal documents can be global (required of all humans) or team-scoped (required when joining a specific team).
- When all required global documents have active consent, the human's consent check status transitions from unset to Pending.
- Legal documents are synced from a GitHub repository by a background job.
- When a new document version is published, existing consents for the old version become stale and re-consent is required.

## Negative Access Rules

- Regular humans **cannot** manage legal documents or document versions.
- ConsentCoordinator **cannot** manage legal documents or versions — they can only review and clear/flag consent checks.
- No one can update or delete consent records. They are permanently immutable.

## Triggers

- When a human signs all required global documents: their consent check status transitions to Pending.
- When a Consent Coordinator clears a consent check: the human is auto-approved as a Volunteer and added to the Volunteers system team.
- When a Consent Coordinator flags a consent check: the human's Volunteer activation is blocked until Board or Admin review.
- When a new document version is published: affected humans are notified to re-consent. A background job sends re-consent reminders.
- A background job suspends humans who no longer have valid consents for required documents.

## Cross-Section Dependencies

- **Profiles:** `IProfileService` — consent-check status lives on the profile (read by `ConsentService` for the review-detail view); `IProfileService.GetActiveApprovedUserIdsAsync` is the fan-out target list when `LegalDocumentSyncService` notifies on a new published / re-consent-required version.
- **Onboarding:** `IOnboardingService.SetConsentCheckPendingIfEligibleAsync` — narrow callback after a consent submit when all required consents are satisfied.
- **Teams:** `ITeamService` — `AdminLegalDocumentService` stitches team names; legal documents are team-scoped (Volunteers team = global).
- **Notifications:** `INotificationService` (in-app fan-out from `LegalDocumentSyncService`) and `INotificationInboxService.ResolveBySourceAsync` (auto-resolve `AccessSuspended` notifications from `ConsentService` once all required consents are complete).
- **Google Integration:** `ISystemTeamSync.SyncVolunteersMembershipForUserAsync` / `SyncCoordinatorsMembershipForUserAsync` — `ConsentService` re-syncs system team membership after each consent submit.
- **Governance:** `IMembershipCalculator.GetRequiredTeamIdsForUserAsync` / `HasAllRequiredConsentsAsync` — `ConsentService` resolves which teams' documents apply to a given user and whether all required consents are complete.

`IGitHubLegalDocumentConnector` is owned by this section (interface in `Humans.Application.Interfaces.Legal`, implementation in `Humans.Infrastructure`); not a cross-section dependency.

## Architecture

**Owning services:** `LegalDocumentService`, `AdminLegalDocumentService`, `LegalDocumentSyncService` (document-side), `ConsentService` (consent-side) — all in `Humans.Application.Services.Legal` / `Humans.Application.Services.Consent`.
**Owned tables:** `legal_documents`, `document_versions`, `consent_records`
**Status:** (A) Migrated, with one cross-domain nav strip deferred (`LegalDocument.Team` — see Repositories block below for details). Both halves of the section live in `Humans.Application.Services.*` and route persistence through owning-section repositories.

### Repositories

- **`ILegalDocumentRepository`** — owns `legal_documents`, `document_versions`.
  - Aggregate-local navs kept: `LegalDocument.Versions`, `DocumentVersion.LegalDocument`.
  - Cross-domain nav `LegalDocument.Team` is still declared on the entity; `ConsentService.GetConsentDashboardAsync` reads it to group consent rows by team. Strip is a follow-up that requires moving to a stitched DTO.
- **`IConsentRepository`** — owns `consent_records`.
  - `consent_records` is append-only per §12 (DB triggers block UPDATE/DELETE) — repository exposes `AddAsync` and `GetXxxAsync` but no `UpdateAsync`/`DeleteAsync`.
  - The lone remaining `.Include` is `GetAllForUserAsync` walking `c.DocumentVersion.LegalDocument` to surface document name + version on the user's consent-history view.
  - Cross-domain navs `ConsentRecord.User` and `ConsentRecord.DocumentVersion` are still declared on the entity; strip is a follow-up.

`LegalDocumentService` (`/Legal` content provider for the Statutes page) has zero `DbContext` usage; it is a thin GitHub-content provider with `IMemoryCache` plus `IGitHubLegalDocumentConnector` for I/O.

`LegalDocumentSyncService` consumes `ILegalDocumentRepository` and delegates all GitHub I/O to `IGitHubLegalDocumentConnector`. Notification fan-out routes the active-approved user lookup through `IProfileService.GetActiveApprovedUserIdsAsync`.

`ConsentService` consumes `IConsentRepository` for all consent-record I/O, calls `ILegalDocumentSyncService` for document/version reads, `IProfileService` for cross-section profile reads, `IOnboardingService` for the consent-pending callback, and `ISystemTeamSync` for post-consent team-membership sync. It also implements `IUserDataContributor` so the GDPR export orchestrator can assemble the per-user consent slice without crossing the section boundary.

### Touch-and-clean guidance

- Never add `UpdateAsync`/`DeleteAsync` paths for `consent_records` — the §12 DB triggers (`prevent_consent_record_update`, `prevent_consent_record_delete`) will raise at runtime. Only `AddAsync` and `GetXxxAsync` are valid on the consent side.
- When editing `ConsentService.GetConsentDashboardAsync`, prefer to plumb a Teams DTO through `ITeamService` rather than re-introducing more `LegalDocument.Team` nav reads — the `.Team` nav is on borrowed time.
