# Legal & Consent — Section Invariants

Legal documents synced from GitHub, per-version consent records (append-only), the Consent Coordinator review gate.

## Concepts

- A **Legal Document** is a named document (e.g., "Privacy Policy", "Volunteer Agreement") that may be global or scoped to a specific team. Documents are synced from a GitHub repository.
- A **Document Version** is a specific revision of a legal document with an effective date and content. When a new version is published, existing consents for the old version may become stale.
- A **Consent Record** is an append-only audit entry linking a human to a specific document version with a timestamp and consent type (granted or withdrawn). Consent records can never be updated or deleted — only new records can be inserted.
- **Consent Check** is the safety gate in the onboarding pipeline. After a human signs all required documents, a Consent Coordinator reviews and either clears or flags the check.

## Data Model

### LegalDocument

**Table:** `legal_documents`

Global or team-scoped. Aggregate-local nav `LegalDocument.Versions` kept. Cross-domain nav `LegalDocument.Team` is **intentionally retained** until `ConsentService` migrates (sub-task #547b) — consent-side reads still navigate it. Scheduled for strip after #547b lands.

### DocumentVersion

**Table:** `document_versions`

Aggregate-local nav `DocumentVersion.LegalDocument` kept.

### ConsentRecord

Append-only per design-rules §12. **DB triggers** prevent UPDATE and DELETE operations; only INSERT is allowed, to maintain GDPR audit-trail integrity.

**Table:** `consent_records`

Cross-domain nav `ConsentRecord.User` — scheduled for strip when `IConsentRepository` lands in #547b.
Cross-aggregate nav `ConsentRecord.DocumentVersion` — scheduled for strip; callers will join by `DocumentVersionId` via `ILegalDocumentRepository`.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Anyone (including anonymous) | View published legal documents |
| Any authenticated human | View own consent status. Sign or re-sign document versions. Accessible during onboarding (before becoming an active member) |
| ConsentCoordinator, Board, Admin | Review consent checks in the onboarding queue. Clear or flag consent checks |
| Board, Admin | Manage legal documents and document versions (create, edit, publish new versions) |

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

- **Profiles:** `IProfileService` — consent check status lives on the profile. Consent completion triggers the onboarding gate.
- **Onboarding:** `IOnboardingEligibilityQuery.SetConsentCheckPendingIfEligibleAsync` — narrow callback when all required consents are satisfied.
- **Teams:** `ITeamService` — legal documents can be scoped to a specific team; joining a team may require consenting to team-specific documents.
- **Google Integration:** `IGitHubLegalDocumentConnector` — legal document sync from GitHub is delegated to an Infrastructure-side connector.

## Architecture

**Owning services:** `LegalDocumentService`, `AdminLegalDocumentService`, `LegalDocumentSyncService` (document-side, migrated peterdrier/Humans PR #547a), `ConsentService` (consent-side, pending in sub-task nobodies-collective/Humans#547b)
**Owned tables:** `legal_documents`, `document_versions`, `consent_records`
**Status:** (B) Partially migrated. Document-side half done — `LegalDocumentService`, `AdminLegalDocumentService`, `LegalDocumentSyncService` live in `Humans.Application.Services.Legal` and share `ILegalDocumentRepository`. GitHub I/O is delegated to `IGitHubLegalDocumentConnector` in Infrastructure. **Consent-side pending** — `ConsentService` still lives in `Humans.Infrastructure/Services` and owns `consent_records`; migration is tracked in sub-task nobodies-collective/Humans#547b.

### Target repositories

- **`ILegalDocumentRepository`** — owns `legal_documents`, `document_versions` — **shipped in PR #547a**
  - Aggregate-local navs kept: `LegalDocument.Versions`, `DocumentVersion.LegalDocument`
  - Cross-domain navs stripped: `LegalDocument.Team` (Teams) — **deferred** until `ConsentService` migrates in #547b so the ConsentService nav read continues to work against the same entity shape.
- **`IConsentRepository`** — owns `consent_records` — **pending in #547b**
  - Aggregate-local navs kept: none — `ConsentRecord` is a flat record
  - Cross-domain navs stripped: `ConsentRecord.User` (Users/Identity), `ConsentRecord.DocumentVersion` (sibling `ILegalDocumentRepository` aggregate — callers join by `DocumentVersionId`).
  - `consent_records` is append-only per §12 (DB triggers block UPDATE/DELETE) — repository exposes `AddAsync` and `GetXxxAsync` but no `UpdateAsync`/`DeleteAsync`.

`LegalDocumentService` has zero `DbContext` usage post-#547a. GitHub I/O is delegated to `IGitHubLegalDocumentConnector` (Infrastructure); `IMemoryCache` usage stays inline on the service and can move into a caching decorator around `ILegalDocumentService` in a follow-up if warranted.

`LegalDocumentSyncService` (post-#547a) lives in `Humans.Application.Services.Legal`, consumes `ILegalDocumentRepository`, and delegates all GitHub I/O to `IGitHubLegalDocumentConnector`. Notification fan-out routes `IsApproved && !IsSuspended` lookups through `IProfileService.GetActiveApprovedUserIdsAsync` instead of reading `_dbContext.Profiles` directly.

### Current violations

Document-side violations cleared in PR #547a. Remaining consent-side (pending #547b, baseline 2026-04-22):

- **Cross-domain `.Include()` calls:**
  - `ConsentService.cs:61` — `.Include(d => d.Team)` (Teams)
  - `ConsentService.cs:67-68` — `.Include(c => c.DocumentVersion).ThenInclude(v => v.LegalDocument)` (cross-aggregate within section; should call `ILegalDocumentRepository` by `DocumentVersionId`)
  - `ConsentService.cs:183-184` — same cross-aggregate shape
- **Cross-section direct DbContext reads:**
  - `ConsentService.cs:110` — reads `_dbContext.Profiles` (Profiles — should call `IProfileService`)
- **Within-section cross-aggregate direct DbContext reads:**
  - `ConsentService.cs:59` — reads `_dbContext.LegalDocuments` (should call `ILegalDocumentRepository`/`ILegalDocumentSyncService`)
- **Cross-domain nav properties on this section's entities:**
  - `LegalDocument.Team` (→ Teams) — still declared; strip scheduled for #547b
  - `ConsentRecord.User` (→ Users/Identity) — strip when introducing `IConsentRepository`
  - `ConsentRecord.DocumentVersion` (→ sibling aggregate) — strip; callers join by `DocumentVersionId`

### Touch-and-clean guidance

- When editing `ConsentService.cs:110`, replace the direct `_dbContext.Profiles` read with an `IProfileService` call.
- When editing `ConsentService.cs:59` or any `LegalDocuments` read in `ConsentService`, route through `ILegalDocumentSyncService`/`ILegalDocumentRepository` rather than adding another `_dbContext.LegalDocuments` query.
- Never add `UpdateAsync`/`DeleteAsync` paths for `consent_records` — §12 DB triggers will reject them at runtime. Only `AddAsync` and `GetXxxAsync` are valid on the consent side.
