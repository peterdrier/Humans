# Legal & Consent — Section Invariants

## Concepts

- A **Legal Document** is a named document (e.g., "Privacy Policy", "Volunteer Agreement") that may be global or scoped to a specific team. Documents are synced from a GitHub repository.
- A **Document Version** is a specific revision of a legal document with an effective date and content. When a new version is published, existing consents for the old version may become stale.
- A **Consent Record** is an append-only audit entry linking a human to a specific document version with a timestamp and consent type (granted or withdrawn). Consent records can never be updated or deleted — only new records can be inserted.
- **Consent Check** is the safety gate in the onboarding pipeline. After a human signs all required documents, a Consent Coordinator reviews and either clears or flags the check.

## Actors & Roles

| Actor | Capabilities |
|-------|-------------|
| Anyone (including anonymous) | View published legal documents |
| Any authenticated human | View own consent status. Sign or re-sign document versions. Accessible during onboarding (before becoming an active member) |
| ConsentCoordinator, Board, Admin | Review consent checks in the onboarding queue. Clear or flag consent checks |
| Board, Admin | Manage legal documents and document versions (create, edit, publish new versions) |

## Invariants

- Consent records are immutable. Database triggers prevent UPDATE and DELETE operations on consent records. Only INSERT is allowed to maintain GDPR audit trail integrity.
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

- **Profiles**: Consent check status lives on the profile. Consent completion triggers the onboarding gate.
- **Onboarding**: Consent to all required documents is a mandatory step before Volunteer activation.
- **Teams**: Legal documents can be scoped to a specific team. Joining a team may require consenting to team-specific documents.
- **Google Integration**: Legal document sync from GitHub is a background job.

## Architecture — Current vs Target

See `docs/architecture/design-rules.md` for the full rules.

**Owning services:** `LegalDocumentService`, `AdminLegalDocumentService`, `LegalDocumentSyncService` (document side, migrated 2026-04-22 in PR #547a), `ConsentService` (consent side, pending in sub-task #547b)
**Owned tables:** `legal_documents`, `document_versions`, `consent_records`

## Target Architecture Direction

> **Status (2026-04-22):** **Document-side half done.** `LegalDocumentService`, `AdminLegalDocumentService`, and `LegalDocumentSyncService` migrated to `Humans.Application.Services.Legal` in PR #547a and share `ILegalDocumentRepository` for `legal_documents` + `document_versions`. GitHub I/O is delegated to `IGitHubLegalDocumentConnector` in Infrastructure. **Consent-side pending** — `ConsentService` still lives in `Humans.Infrastructure/Services` and owns `consent_records`; migration is tracked in sub-task #547b. The cross-aggregate nav `LegalDocument.Team` was intentionally left declared (not stripped) so ConsentService continues to read it from the same entity shape until #547b lands.

### Target repositories

- **`ILegalDocumentRepository`** — owns `legal_documents`, `document_versions` — **shipped in PR #547a**
  - Aggregate-local navs kept: `LegalDocument.Versions`, `DocumentVersion.LegalDocument`
  - Cross-domain navs stripped: `LegalDocument.Team` (Teams section) — **deferred** until `ConsentService` migrates in #547b so the ConsentService nav read continues to work against the same entity shape.
- **`IConsentRepository`** — owns `consent_records` — **pending in #547b**
  - Aggregate-local navs kept: (none — `ConsentRecord` is a flat record)
  - Cross-domain navs stripped: `ConsentRecord.User` (Users/Identity section), `ConsentRecord.DocumentVersion` (sibling `ILegalDocumentRepository` aggregate — callers that need version data should call `ILegalDocumentService`/`ILegalDocumentRepository` by `DocumentVersionId`)
  - Note: `consent_records` is append-only per §12 (DB triggers block UPDATE/DELETE) — repository exposes `AddAsync` and `GetXxxAsync` but no `UpdateAsync`/`DeleteAsync`.

`LegalDocumentService` (the GitHub markdown fetcher) has zero `DbContext` usage and post-#547a lives in `Humans.Application.Services.Legal`. GitHub I/O is delegated to `IGitHubLegalDocumentConnector` (Infrastructure); the `IMemoryCache` usage stays inline on the service for now and can move into a caching decorator around `ILegalDocumentService` in a follow-up if warranted.

`LegalDocumentSyncService` (post-#547a) lives in `Humans.Application.Services.Legal`, consumes `ILegalDocumentRepository`, and delegates all GitHub I/O to `IGitHubLegalDocumentConnector`. Notification fan-out routes `IsApproved && !IsSuspended` lookups through `IProfileService.GetActiveApprovedUserIdsAsync` instead of reading `_dbContext.Profiles` directly.

### Current violations

Document-side violations cleared in PR #547a. Remaining pending items observed as of 2026-04-22 (all consent-side, migrating in #547b):

- **Cross-domain `.Include()` calls:**
  - `ConsentService.cs:61` — `.Include(d => d.Team)` (Teams section)
  - `ConsentService.cs:67-68` — `.Include(c => c.DocumentVersion).ThenInclude(v => v.LegalDocument)` (cross-aggregate within section; should call `ILegalDocumentRepository` by `DocumentVersionId`)
  - `ConsentService.cs:183-184` — same cross-aggregate shape
- **Cross-section direct DbContext reads:**
  - `ConsentService.cs:110` — reads `_dbContext.Profiles` (Profiles section; should call `IProfileService`)
- **Within-section cross-aggregate direct DbContext reads:**
  - `ConsentService.cs:59` — reads `_dbContext.LegalDocuments` (should call `ILegalDocumentRepository` / `ILegalDocumentSyncService`)
- **Cross-domain nav properties on this section's entities:**
  - `LegalDocument.Team` (→ `Teams`, Teams section) — still declared; strip scheduled for #547b once `ConsentService` stops navigating it.
  - `ConsentRecord.User` (→ `User`, Users/Identity section) — strip when introducing `IConsentRepository`.
  - `ConsentRecord.DocumentVersion` (→ `DocumentVersion`, sibling aggregate) — strip; callers join by `DocumentVersionId` through `ILegalDocumentRepository`.

### Touch-and-clean guidance

Until `ConsentService` migrates in #547b, when touching its code:

- When editing `ConsentService.cs:110`, replace the direct `_dbContext.Profiles` read with an `IProfileService` call.
- When editing `ConsentService.cs:59` or any `LegalDocuments` read in `ConsentService`, route through `ILegalDocumentSyncService`/`ILegalDocumentRepository` rather than adding another `_dbContext.LegalDocuments` query.
- Never add `UpdateAsync`/`DeleteAsync` paths for `consent_records` — §12 DB triggers will reject them at runtime. Only `AddAsync` and `GetXxxAsync` are valid on the consent side.
