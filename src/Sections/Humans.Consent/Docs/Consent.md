<!-- freshness:triggers
  src/Sections/Humans.Consent/**
  src/Sections/Humans.Consent/Contracts/**
  src/Sections/Humans.Onboarding/**
  src/Sections/Humans.Teams/Services/SystemTeamSyncJob.cs
-->
<!-- freshness:flag-on-change
  ConsentRecord append-only DB-trigger invariant, document sync from GitHub, and the Consent Coordinator review queue (audit-only, NOT a gate for Volunteers admission) — review when Legal/Consent services/entities/controllers change.
-->

# Consent — Section Invariants

Legal documents synced from GitHub, per-version consent records (append-only), the Consent Coordinator audit/review queue.

## Concepts

- A **Legal Document** is a named, team-scoped document (e.g., "Privacy Policy", "Volunteer Agreement"). Documents on the Volunteers system team apply to every active human. Each document points at a folder in the configured GitHub repository and is synced from there by `LegalDocumentSyncService` / `SyncLegalDocumentsJob`.
- A **Document Version** is a specific revision of a legal document with an `EffectiveFrom` instant and a multi-language `Content` dictionary keyed by language code (Spanish `"es"` is canonical/legally binding). When the GitHub commit SHA for the canonical file changes, the sync produces a new version; if `RequiresReConsent` is true, affected users are re-notified.
- A **Consent Record** is an append-only audit entry linking a user to a specific document version with timestamp, IP, user-agent, content hash, and an `ExplicitConsent` flag. Consent records can never be updated or deleted — only new records can be inserted.
- **Consent Check** is an audit/annotation track Consent owns in `consent_reviews` (`ConsentReviewInfo.Status`; Users decomposition commit 12 moved it off `profiles`). After a human signs all required documents, the status flips to `Pending` and the human appears in the Consent Coordinator review queue. CC actions (Clear / Flag / Reject) maintain the annotation but do NOT gate admission to the Volunteers team — admission is automatic once profile + consents are complete.
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

Aggregate-local nav `LegalDocument.Versions` kept. Cross-domain nav `LegalDocument.Team` has been removed — `LegalDocument.cs` carries `TeamId` only. `LegalDocumentRepository` no longer `.Include(d => d.Team)`; `LegalDocumentSyncService` resolves team names via `ITeamService.GetTeamsWithParentsAsync` and stitches `TeamName` onto its DTOs, and `ConsentService.GetConsentDashboardAsync` groups dashboard rows on the stitched `TeamId`/`TeamName` pair instead of walking a nav.

**Cross-domain FK:** `TeamId` → Teams section — **FK only**, no navigation property. The `Team.LegalDocuments` reverse nav has also been removed from the Teams entity.

### DocumentVersion

**Table:** `document_versions`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| LegalDocumentId | Guid | FK → `legal_documents` |
| VersionNumber | string (50) | Display label |
| CommitSha | string (40) | |
| Content | jsonb | `Dictionary<string, string>` keyed by language code; `"es"` is canonical/legally binding; equality and hashing ignore insertion order |
| EffectiveFrom | Instant | |
| RequiresReConsent | bool | |
| CreatedAt | Instant | |
| ChangesSummary | string? (2000) | Imported GitHub commit first lines are capped at 500 UTF-16 units without splitting a surrogate pair |

Aggregate-local nav `DocumentVersion.LegalDocument` kept. Aggregate-local nav `DocumentVersion.ConsentRecords` declared on the entity and configured in `DocumentVersionConfiguration`; not currently walked by the service layer.

### ConsentRecord

Append-only per design-rules §12. **DB triggers** (`prevent_consent_record_update` / `prevent_consent_record_delete`, both calling `prevent_consent_record_modification()`) raise an exception on any UPDATE or DELETE against `consent_records`; only INSERT is allowed, to maintain GDPR audit-trail integrity. `IConsentRepository` exposes only `Add`/`Get` methods to match.

**Table:** `consent_records`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| UserId | Guid | FK → Users section |
| DocumentVersionId | Guid | FK → `document_versions` |
| ConsentedAt | Instant | |
| IpAddress | string (45) | IPv6-capable; service passes value through unchanged |
| UserAgent | string (1024) | Service caps at 500 UTF-16 units without splitting a surrogate pair |
| ContentHash | string (64) | SHA-256 hex of canonical Spanish content at consent time |
| ExplicitConsent | bool | Always true for valid records |

`ConsentService.SubmitConsentAsync` refuses unchecked consent before writing any record or recording consent metrics. The document remains signable on a later explicit submission; UI checkbox guards are an additional check.

**Unique index:** `(UserId, DocumentVersionId)` — prevents duplicate consents for the same version.

Cross-aggregate nav `ConsentRecord.DocumentVersion` — still declared and walked by `ConsentRepository.GetAllForUserIdsAsync` (`.Include(c => c.DocumentVersion).ThenInclude(v => v.LegalDocument)`) to surface document name + version number on the user's consent-history view.

Per-user reads on `consent_records` chain-follow merge tombstones via `UserInfo.AllUserIds` of the record `IUserService.GetUserInfoAsync(userId)` returned (survivor first, then `MergedUserIds`; never the requested id) so consents signed under a now-merged source id surface for the fold target, and a read through a merged-away id sees the survivor's consents. Consent records stay at source after merge by design, DB triggers make any rewrite physically impossible, so `AnonymizeForMergeAsync` cannot move them.

### ConsentReview

**Table:** `consent_reviews` — the Consent Coordinator's review of a member: one row per user id, written by `ConsentReviewService` (the only writer, through `IConsentReviewRepository`).

| Property | Type | Notes |
|----------|------|-------|
| UserId | Guid | PK; FK-only → Users section |
| Status | `ConsentCheckStatus`? (string, 20) | Pending / Cleared / Flagged; null until all required consents are signed |
| CheckedAt | Instant? | Last clear/flag |
| CheckedByUserId | Guid? | Reviewer |
| Notes | string? (4000) | Coordinator notes; erased on Art. 17 |
| IsApproved | bool | True on Cleared, false on Flagged or Reject. An audit annotation; Volunteers admission never reads it |
| RejectionReason | string? (4000) | Erased on Art. 17 |
| RejectedAt | Instant? | Set by Reject; a rejected review cannot be cleared or rejected again |
| RejectedByUserId | Guid? | |
| UpdatedAt | Instant | |

- **Legacy fallback.** Until the `consent.profile-review` data migration reports done in production, a member with no row reads the frozen `profiles` review columns through `UserInfo` (`[Obsolete(HUM_PROFILE_REVIEW)]`, one suppressed reader, `ConsentReviewService.LegacyReview`). A write to a member with no row starts from that legacy review. The copy also records a legacy rejection as `users.State = Rejected` where the stored state never did.
- **Merge.** No `IUserMerge`: rows stay keyed to the id they were written for, and reads resolve a merged-away id forward via `GetUserInfoAsync`, so the survivor's own review stands — as when these were profile columns the profile fold never moved.
- **The rejection state lives in Users.** Reject writes the record here (`IConsentWrite.RecordRejectionAsync`) and the state there (`IUserWrite.SetRejectedAsync`); every gate reads `UserInfo.IsRejected` / the stored `UserState.Rejected` or `ConsentReviewInfo.IsRejected`.
- **GDPR.** Export slice `ConsentReview` (the rows of every id in the member's merge chain). Erasure keeps status, dates, approval and reviewer ids (Art. 17(3)(e) — how the association admitted or rejected the member) and erases the notes and the rejection reason.

## Routing

Three controllers serve this section.

| Controller | Route prefix | Auth |
|------------|-------------|------|
| `LegalController` | `/Legal/{slug?}` | `[AllowAnonymous]` — Statutes page only |
| `ConsentController` | `/Consent` (conventional) | `[Authorize]` |
| `MyDataController` | `/Consent/MyData` | `[Authorize]` |
| `AdminLegalDocumentsController` | `/Legal/Admin/Documents` | `[Authorize(Policy = PolicyNames.BoardOrAdmin)]` |

**Consent routes:**

| Route | Method | Action |
|-------|--------|--------|
| `/Consent` | GET | Dashboard (team-grouped document status) |
| `/Consent/Review?id={versionId}` | GET | Document review before consent |
| `/Consent/Submit` | POST | Record consent |
| `/Consent/MyData` | GET | My Data page shell: Gdpr's export/deletion actions, the pending-deletion status (read from `UserInfo`) and the member's signed documents (consent history). Exempt from `MembershipRequiredFilter` so non-Active members reach it |

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

Document archival uses the shared `data-confirm` handler: each click prompts once, and cancellation prevents submission. The team filter still submits when its selection changes.

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Anyone (including anonymous) | View `/Legal` Statutes page |
| Any authenticated human | View own consent dashboard at `/Consent`. Sign or re-sign document versions. Accessible during onboarding (before becoming an active member) |
| ConsentCoordinator, ShiftsAdmin, Board, Admin | Read access to the onboarding review queue at `/OnboardingReview` (`PolicyNames.ReviewQueueAccess`) |
| ConsentCoordinator, Board, Admin | Clear, Flag, or Reject consent checks (`PolicyNames.ConsentCoordinatorBoardOrAdmin`) |
| Board, Admin | Manage legal documents and document versions at `/Legal/Admin/Documents` (`PolicyNames.BoardOrAdmin`): create, edit, archive, trigger manual sync, edit version summaries |

## Invariants

- Legal-document forms require explicit required/active flags and grace-period days; omission redisplays field errors before any write and cannot reactivate a document, change its consent requirement or reset its grace period. Explicit false flags remain valid.

- Legal-document creation and edits refuse empty or nonexistent owning teams with 404 before persistence, initial sync, cache invalidation or success audit. The service checks the team through `ITeamService`; existing inactive teams remain valid owners.

- Manual version summaries are optional and trimmed. Summaries over 2000 characters are refused before persistence, cache invalidation or success audit; the admin editor shows the refusal. Blank summaries clear the existing value.

- Canonical Spanish content must be non-blank. Sync refuses an empty canonical file before
  publishing a version or changing its SHA. Review treats missing/blank canonical content as
  unavailable, and submission records no consent or metrics for it (`ContentUnavailable`).
  The consent page uses its existing localized failure message; onboarding's unavailable-document
  path handles an unavailable review. Hashes always cover the exact canonical text.

- The consent dashboard renders section service DTOs directly, sorting pending teams/documents before signed ones and then by ordinal name. It retains UTC date display and the ten most recent history rows; the controller does not copy DTOs into duplicate row models.

- The member consent dashboard and review pages localize breadcrumb navigation labels in all six supported cultures. Consent resources consistently use `Consent_`, including the shared review form consumed by Onboarding and the empty-state message reused by Governance.
- Anonymous Legal pages use the existing six-culture content-unavailable message when GitHub content is absent.
- The shared document partial renders the supplied localized empty message for zero translations as well as empty translation text; unavailable Legal content does not leave a blank card.
- Legal and consent document tabs retain native language names and localize the canonical Spanish legal marker in all six cultures.
- Legal content reads retain caller cancellation through GitHub reads, including anonymous Legal and Governance pages. Caller-cancelled reads propagate without caching unavailable content; already-cancelled callers also stop on cache hits. A fetch timeout without caller cancellation degrades to unavailable content like any other GitHub failure.

- Consent dashboard and review reads carry request cancellation through viewer resolution, the stub-profile check and dashboard/document reads. Submit and its invalid-form redisplay retain their existing mutation boundaries.
- Legal-document admin list, create and edit GETs retain request cancellation through document reads and the active-team picker. POST redisplay reads keep their mutation boundary.

- Consent records are immutable. Database triggers prevent UPDATE and DELETE operations on `consent_records`. Only INSERT is allowed to maintain GDPR audit trail integrity (§12).
- Required-version snapshots for all documents and for a single team share the same latest-effective-version selection; documents without an effective version are omitted.
- Legal documents can be global (required of all humans) or team-scoped (required when joining a specific team).
- Sync emails consolidate only outstanding required documents belonging to each recipient’s teams. Optional updates and required updates already signed by that recipient do not trigger a consent email. Emails use the recipient’s supported saved language, with English fallback for missing or unsupported preferences.
- When all required global documents have active consent, the human's consent check status transitions from unset to Pending.
- Legal documents are synced from a GitHub repository by a background job.
- Synced version content is fetched by each file’s immutable blob SHA, after resolving the latest commit changing that path on the configured branch and pinning its metadata read to that commit. The canonical Spanish content and version SHA come from the same fetch, reused without a second branch read. Change summaries use that canonical file’s commit reference; the blob SHA remains the version identity.
- GitHub document reads use the configured `GitHub:Branch`, including both directory discovery and translated file content for the anonymous `/Legal` pages.
- When a new document version is published, existing consents for the old version become stale and re-consent is required.
- Per-user reads on `consent_records` chain-follow merge tombstones via the resolved record's `UserInfo.AllUserIds` so consents signed under a now-merged source id surface for the fold target. Consent records stay at source after merge, DB triggers (`prevent_consent_record_update`, `prevent_consent_record_delete`) make any rewrite physically impossible.
- New signatures use the resolved survivor ID, including when a widget submits an archived account ID. Required-consent checks and post-save suspension-notification cleanup use that same ID; existing immutable signatures keep their original attribution.

- Once a consent record is saved, required-consent checks, suspension-notification resolution, suspension restoration, and inline cache refreshes finish independently of request cancellation. The write evicts every merge-chain cache entry before the required-consent check, so suspension cleanup sees the new signature even when the cache was warm. Validation and the consent write retain the caller token.
- A successful consent submission evicts every affected merge-chain cache key before inline reloads start. A failed reload therefore leaves no alias serving the pre-submit consent set; the failure propagates and later reads reload the committed state.

## Negative Access Rules

- Regular humans **cannot** manage legal documents or document versions.
- ConsentCoordinator **cannot** manage legal documents or versions — they can only review and clear/flag consent checks.
### ConsentReminder

**Table:** `consent_reminders` — when the re-consent reminder email last went to a member: one row per user id, written by `ConsentReminderService` (the only writer, through `IConsentReminderRepository`) for `SendReConsentReminderJob`.

| Property | Type | Notes |
|----------|------|-------|
| UserId | Guid | PK; FK-only → Users section |
| LastSentAt | Instant | The cooldown anchor; stamped after each successful enqueue |

- **Single owner.** Reminder cooldown and export reads use only `consent_reminders`. The `consent.reminder-stamps` copy and legacy Users read were retired after production `GET https://humans.nobodies.team/api/data-migrations` reported `{"key":"consent.reminder-stamps","pending":0,"done":true}` on 2026-10-22. The Users column awaits a separate drop after this retirement deploys.
- **Merge.** No `IUserMerge`: rows stay keyed to the id they were written for; the job reads by the resolved survivor id.
- **GDPR.** Export slice `ConsentReminder` (the stamps of every id in the member's merge chain, survivor first). Erasure deletes the member's row.

- No one **can** update or delete consent records. They are permanently immutable.

GitHub document discovery, metadata/raw content, commit-summary and prefix-content reads honour their existing caller token before fetching and while waiting. Cancellation propagates rather than becoming a missing summary or continuing to the next fetch; already-started Octokit requests may finish in the background.

## Triggers

- When a human signs all required global documents: their consent check status transitions to Pending. `ConsentService.SubmitConsentAsync` fires no per-user team sync — Volunteers admission is reconciled by the scheduled `SystemTeamSyncJob.SyncVolunteersTeamAsync` pass on name + consents (eventually consistent). App access never depended on Volunteers membership.
- When a Consent Coordinator clears a consent check: the review's `IsApproved` is set to true and `Status = Cleared` (`IConsentWrite.RecordConsentCheckAsync`). This is an audit annotation only — `ClearConsentCheckAsync` provisions no team; Volunteers membership and app access are independent of CC review.
- When a Consent Coordinator flags a consent check: the review's `IsApproved` is set to false and `Status = Flagged`. This is an audit annotation only — `FlagConsentCheckAsync` provisions/deprovisions no team; Volunteers membership and app access are unaffected. `RejectSignupAsync` (the review's `RejectedAt` here, `UserState.Rejected` in Users) is the CC's only actual kick-out lever.
- When a new document version is published: affected humans are notified to re-consent. A background job sends re-consent reminders.
- Re-consent reminder emails render in each recipient’s supported preferred language, with English fallback for blank, malformed or unsupported preferences.
- The reminder job skips a member stamped in `consent_reminders` within `ConsentReminderCooldownDays` and stamps each member it reminds. It attempts every eligible recipient before reporting per-recipient preparation, enqueue, or cooldown-stamp failures. Failed enqueues do not stamp the cooldown; successful reminders retain it. Collected failures still fail the job so Hangfire can retry, skipping recipients already in cooldown.
- A background job suspends humans who no longer have valid consents for required documents.

- Legal document names in notices may outgrow the 200-character title limit. Consent sends full copy to Notifications, which bounds stored previews by Unicode character, preserving the full title and existing detail in the body; short notice copy is unchanged.

- Successful document create, settings edit, archive, and version-summary edit write audit events attributed to the Board/Admin actor, after persistence. A created document remains audited if initial GitHub sync fails.
- Every persisted new document version writes a publication audit linked to its version ID, before notification fan-out: manual and initial sync name the initiating administrator; scheduled sync names `LegalDocumentSyncService`. Missing rows, failed publication and unchanged-content sync touches do not emit publication events. Signed consent records remain immutable and separate.

## Cross-Section Dependencies

- **Profiles/Onboarding:** `ConsentService` has no profile dependency and does **not** call into Profile or Onboarding after a consent submit — the controller (`ConsentController.Submit`, `OnboardingWidgetController`) reports the signature with `IOnboardingIntake.ConsentSignedAsync` after `ConsentService.SubmitConsentAsync`, and Onboarding runs the threshold check.
- **Teams:** `ITeamService` — `LegalDocumentSyncService` stitches team names in memory (replaces `.Include(d => d.Team)`); legal documents are team-scoped (Volunteers team = global).
- **Email:** Consent owns its two templates — `ConsentEmails` (internal) builds each `EmailMessage` from Consent's own `Consent_Email_*` keys in `ConsentResource`, rendered in the recipient's culture via `CultureScope`; `ConsentEmailPreviews` (`IEmailPreviewContributor`, registered in `Section.Register`) lists them at `/Email/EmailPreview`. Email supplies transport only — `IEmailService.SendAsync` (`memory/architecture/email-templates-live-in-sender.md`, peterdrier/Humans#1651).
- **AuditLog:** `IAuditLogService` records legal-document administration and version publication through its existing write contract.
- **Notifications:** `Humans.Notifications.Contracts.INotificationEmitter` (in-app fan-out from `LegalDocumentSyncService`) and `INotificationAutoResolve.ResolveBySourceAsync` (auto-resolve `AccessSuspended` notifications from `ConsentService` once all required consents are complete — the narrow auto-resolve contract, not the full inbox service).
- **Human Lifecycle:** `IHumanLifecycleService.RestoreConsentSuspensionAsync` — `ConsentService` lifts a consent suspension once all required consents are complete (alongside resolving the `AccessSuspended` notification). `ConsentService` does not depend on `ISystemTeamSync` — a consent submit provisions no system-team membership; the scheduled `SystemTeamSyncJob` reconciles Volunteers/Coordinators on name + consents.
- **Governance:** `IMembershipCalculator.GetRequiredTeamIdsForUserAsync` / `HasAllRequiredConsentsAsync` — `ConsentService` resolves which teams' documents apply to a given user and whether all required consents are complete.
- **Gdpr:** the My Data page posts to Gdpr's `MemberDataController` (export, request deletion) and `UserController` (cancel deletion); Consent references Gdpr's project only for those routes and its existing contributor types. Pending-deletion dates come from `UserInfo`.
- **Users/Identity:** `IUserService.GetUserInfoAsync` / `GetUserInfosAsync`, the returned `UserInfo.AllUserIds` chain-follows merge tombstones on every per-user consent read so consents signed under a source id surface for the fold target, and the read resolves a tombstone id forward to its survivor. Consent records are immutable per §12 and stay at source. `IUserService.GetAllUserInfosAsync` (filtered to `IsActive`) is the in-app notification fan-out list when `LegalDocumentSyncService` publishes a new / re-consent-required version; notices are grouped by supported recipient language in all six cultures (English fallback), with delivery failures isolated per group. Only the email pass (`LegalDocumentSyncRunner`) narrows to affected team members.

`IGitHubLegalDocumentConnector` is owned by this section (interface and implementation both `internal` in `Humans.Consent.Services`); not a cross-section dependency.

## Architecture

A delayed per-key or bulk consent load cannot republish its old result after an intervening cache eviction or refresh.

**Owning services:** `LegalDocumentService` (Statutes page), `LegalDocumentSyncService` (document-side — sole writer for `legal_documents`/`document_versions`, owning both the admin write surface `IAdminLegalDocumentService` and the GitHub-sync write surface `ILegalDocumentSyncManagement`; nobodies-collective/Humans#751), `ConsentService` (consent-side), `LegalDocumentSyncRunner` (the GitHub sync + re-consent fan-out body) — all in `Humans.Consent.Services`, `internal sealed`. `SyncLegalDocumentsJob` itself is `Humans.Consent/Jobs/SyncLegalDocumentsJob.cs` — `public` because Shell names the concrete type when it registers and schedules it.
**Owned tables:** `legal_documents`, `document_versions`, `consent_records`, `consent_reviews`, `consent_reminders`
**Status:** (G5) Own project — `src/Sections/Humans.Consent` + `src/Sections/Humans.Consent/Contracts` (nobodies-collective/Humans#866). Owns `LegalDbContext` and its migrations; entities, repositories, services, controllers, views and `ConsentResource` are all internal to the assembly. All cross-domain navs (`LegalDocument.Team`, `Team.LegalDocuments`) have been stripped. The context keeps the name `LegalDbContext` and the `legal_*` table names — a G5 move changes files, never the schema (nobodies-collective/Humans#1012).

- Services live in `Humans.Consent/Services/` and take no `DbContext`, `IDbContextFactory<>` or store type in their constructors.
- `ILegalDocumentRepository` (impl `LegalDocumentRepository` in `Humans.Consent/Data/`) is the only code path that touches `legal_documents` and `document_versions` via `DbContext`.
- `IConsentRepository` (impl `ConsentRepository` in `Humans.Consent/Data/`) is the only code path that touches `consent_records` via `DbContext`. Exposes `AddAsync` and `GetXxxAsync` only — no `UpdateAsync`/`DeleteAsync`.
- `IConsentReviewRepository` (impl `ConsentReviewRepository`) is the only code path that touches `consent_reviews`.
- `IConsentReminderRepository` (impl `ConsentReminderRepository`) is the only code path that touches `consent_reminders`.
- **Decorator decision (T-04, 2026-05-16)** — Two-layer cache landed:
  - **Global** — `CachingLegalDocumentSyncService` (Singleton in `Humans.Consent.Services`) wraps `LegalDocumentSyncService` (inner, keyed Scoped; also the sole writer, implementing both `ILegalDocumentSyncManagement` and `IAdminLegalDocumentService`). Holds the active+required document set as `ActiveRequiredLegalDocumentSnapshot[]` keyed by document id, plus a version-id → document-id index. Serves `GetActiveRequiredDocumentsForTeamsAsync`, `GetRequiredDocumentVersionsForTeamAsync`, `GetRequiredVersionsAsync`, `GetVersionByIdAsync` from cache. `LegalDocumentSyncService` calls `CachingLegalDocumentSyncService.InvalidateAll()` directly after each successful repository write (before the publish notification fan-out and the next document of a full sync, so the eviction stays in the writer rather than the decorator's pass-through) (admin create/update/archive/version-summary, GitHub-sync version add/touch) — there is no SaveChanges interceptor (nobodies-collective/Humans#751). Eager warmup at startup: the decorator inherits `TrackedCache<Guid, ActiveRequiredLegalDocumentSnapshot>` with `warmOnStartup: true`, so its own `IHostedService.StartAsync` drives `WarmAllAsync` at boot (non-fatal); no separate warmup hosted service. Warmup reads the inner service’s enriched `GetActiveRequiredDocumentsForTeamsAsync(null)` snapshot; only the inner service resolves team names via `ITeamService.GetTeamsWithParentsAsync`. Null selects every team, while an empty collection selects none. The decorator preserves ascending version order and builds its version index without resolving another section’s services.
  - **Per-user** — `CachingConsentService` (Singleton in `Humans.Consent.Services`) wraps `ConsentService` (inner, keyed Scoped). Holds `UserConsentInfo` (the user's explicitly consented version-id set, with the merge source-id chain unioned at warm time) keyed by user id. Serves `GetConsentedVersionIdsAsync` and `GetConsentMapForUsersAsync` from cache. Required-consent row composition delegates to the inner service, which owns the legal-document and clock reads. Lazy per-user warm. **Synchronous refresh on `SubmitConsentAsync`**: the decorator override refreshes the user (and any merged-source-id tombstones) via `ReplaceAsync` inline before returning, so the controller's next-page consent-banner check observes the fresh state. Each entry carries the merge-chain ids it was built over; the inner service supplies current chains through its internal `GetUserIdChainsAsync` bulk read. The decorator resolves no Users or legal-document service. A read whose current list differs reloads it, so after an account merge tombstones the source, both the survivor's and the source id's entries rebuild against the new chain on their next read, with no signal from Users.
  - `LegalDocumentService` (Statutes page) keeps its own `IMemoryCache` for the GitHub-fetched anonymous statutes content (zero DB access, pure I/O cache; out of scope for T-04).
  - Architecture tests pin the decorators' dual-interface shape: `ConsentArchitectureTests.{CachingConsentService_ImplementsBothServiceAndInvalidator, CachingLegalDocumentSyncService_ImplementsBothServiceAndInvalidator, CachingConsentService_DeclaresSubmitConsentAsync}`.
- **Public contract.** `IConsentService` (8 methods: `GetConsentedVersionIdsAsync`, `GetConsentMapForUsersAsync`, `GetRequiredConsentRowsForUserAsync`, `GetPendingDocumentNamesAsync`, `GetConsentRecordCountAsync`, `GetConsentReviewDetailAsync`, `GetConsentReviewAsync`, `GetConsentReviewsAsync`) is the public contract — only Consent projections, no EF entities. The two review reads pass through `CachingConsentService` uncached. `IConsentWrite` (`RecordConsentCheckAsync`, `SetConsentCheckPendingAsync`, `RecordRejectionAsync`) is the review's write, for Onboarding's coordinator actions and the Tickets/Development seeders; `ConsentReviewInfo`, `ConsentCheckStatus` and the queue predicates `ConsentReviewPredicates.NeedsConsentReview` / `IsConsentCheckFlagged` sit beside it. `IConsentSubmission` carries the one write that leaves the section (`SubmitConsentAsync`), for Shell's onboarding widget and `DevPersonaSeeder`. Both sit on `Humans.Consent.Contracts` alongside `ILegalDocumentService` (statutes), `ILegalDocumentSyncService` (one member, `GetRequiredDocumentVersionsForTeamAsync`, for Governance's membership calculator; the reminder job and metrics snapshot use the internal `ILegalDocumentSyncManagement`), `ILegalDocumentSyncRunner` (what `SyncLegalDocumentsJob` does, as one call) and `ConsentReviewFormViewModel` (the model Shell builds for the section's `_ConsentReviewBody` partial). The internal `IConsentManagement : IConsentService, IConsentSubmission` adds `GetConsentDashboardAsync` and the decorator’s live merge-chain lookup; the admin legal-document surface, the GitHub connector and the sync service's write half never leave.
- **Cross-domain navs removed:** `LegalDocument.Team` and its `Team.LegalDocuments` reverse collection are both gone; `LegalDocumentRepository` no longer `.Include(d => d.Team)`. `LegalDocumentSyncService.GetActiveRequiredDocumentsForTeamsAsync` and `ConsentService.GetConsentDashboardAsync` resolve team names via `ITeamService.GetTeamsWithParentsAsync`, stitched onto the DTO.
- **Aggregate-local navs still declared (not cross-section):**
  - `ConsentRecord.DocumentVersion` — walked by `ConsentRepository.GetAllForUserIdsAsync` (`.ThenInclude(v => v.LegalDocument)`) to surface document name + version on the consent-history view. This is aggregate-local for `consent_records` → `document_versions` → `legal_documents`; not a cross-section nav.
  - `DocumentVersion.ConsentRecords` — declared and configured (`DocumentVersionConfiguration`); not navigated by any current service path.
- **Cross-section calls:** `ITeamService`, `INotificationEmitter`, `INotificationAutoResolve`, `IHumanLifecycleService`, `IMembershipCalculator`, `IUserService`, `IUserWrite` (`SetRejectedAsync`, from the `consent.profile-review` copy only).
- **User parts.** `ConsentReviewCardViewComponent` on `UserPartSlots.AdminDetail` (English, admin view mode only: the consent-check badge and, for a Rejected account, the reason, time and reviewer) and `RejectionReasonViewComponent` on `UserPartSlots.AccountStatus` (Gdpr's status wall, Self view mode, `Consent_AccountStatus_ReasonLabel` in six cultures).
- **Architecture tests:** `tests/Humans.Consent.Tests/Architecture/LegalArchitectureTests.cs` (Octokit containment — the sync service must not reference Octokit), `tests/Humans.Consent.Tests/Architecture/ConsentArchitectureTests.cs` (ConsentService, IConsentRepository append-only shape). Page rendering — the §15 step 12 check — is `tests/Humans.Integration.Tests/Controllers/ConsentPageRenderTests.cs`.

### Touch-and-clean guidance

- `DocumentVersion.ConsentRecords` — declared but not navigated by any service, view, or test. Stripping it is **not** doc-only: `DocumentVersionConfiguration` is the sole declaration of the `consent_records` → `document_versions` FK and its `OnDelete(Restrict)`, so the relationship must be re-expressed from the `ConsentRecord.DocumentVersion` side and a `dotnet ef migrations add` must produce an empty `Up()` to prove the schema is unchanged (see Users' #635 nav-strip precedent).

## Issue queue

`Section` also declares this queue’s Area-label resource key and page-path aliases; Issues discovers both with the queue.

Consent owns the `Legal` issue queue: it implements `IIssueQueueOwner` (Issues' `Contracts/`
folder) on its `Section` entry point, declaring the queue key and the roles that handle
issues filed against it — `ConsentCoordinator`, plus `Admin`, which handles every queue.
Issues discovers the declaration through DI and holds no list of sections; dropping the
seam sends this section's stored issues to the Admin-only queue. The key is `Legal`, not
`Consent`: the section was renamed at the 2026-08-03 freeze and stored rows still carry
the old string, so the queue keeps its name (and `/Debug/Sections` lists it as an
unmatched annotation, which is the rename showing rather than hiding).

Re-consent reminders select active-assignment users with unsigned required consents whose grace deadlines fall within `ConsentReminderDaysBeforeSuspension`, including overdue deadlines. The configured lead time controls eligibility as well as email wording. The reminder cooldown and successful-send timestamp remain unchanged; suspension still uses only expired deadlines.
