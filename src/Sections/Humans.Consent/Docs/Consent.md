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

Consent is where the association proves, to a regulator or a court, that every member agreed to the current text of every document that applies to them. It keeps the legal documents in step with the legal team's GitHub repository, shows each member what they still owe, records each signature as a permanent tamper-proof entry, and reminds members before a deadline lapses. It also stores the Consent Coordinator's verdict on each new member (cleared, flagged, rejected with a reason).

Good looks like: a signature can never be altered or lost, a member is never asked to sign something they already signed (including under a merged account), and a new document version reaches the right people the night it is published.

Deliberate choices: Spanish is the binding text and the only one hashed; completing consents never gates app access and provisions nothing (the system-team sync reconciles membership); the Coordinator's verdict is an annotation, not a gate.

Out of scope: deciding who is a member (Governance), suspending for missing consents (Users), the sign-up rejection workflow the queue's Reject triggers (Onboarding), sending mail (Email).

Bulk cache warmups publish through the shared mutation guard. A refresh, deletion, eviction or clear overlapping the load discards the partial attempt and reloads the full snapshot and derived indexes before a read can treat the cache as warmed.

## Concepts

- A **Legal Document** is a named, team-scoped document (e.g., "Privacy Policy", "Volunteer Agreement"). Documents on the Volunteers system team apply to every active human. Each document points at a folder in the configured GitHub repository and is synced from there by `LegalDocumentSyncService` / `SyncLegalDocumentsJob`.
- A **Document Version** is a specific revision of a legal document with an `EffectiveFrom` instant and a multi-language `Content` dictionary keyed by language code (Spanish `"es"` is canonical/legally binding). When the GitHub commit SHA for the canonical file changes, the sync produces a new version; if `RequiresReConsent` is true, affected users are re-notified.
- A **Consent Record** is an append-only audit entry linking a user to a specific document version with timestamp, IP, user-agent, content hash, and an `ExplicitConsent` flag. Consent records can never be updated or deleted — only new records can be inserted.
- **Consent Check** is an audit/annotation track Consent owns in `consent_reviews` (`ConsentReviewInfo.Status`; Users decomposition commit 12 moved it off `user_profiles`). After a human signs all required documents, the status flips to `Pending` and the human appears in the Consent Coordinator review queue. Fake accounts (Users' `UserInfo.IsFake`: the gate terminal, dev personas) never appear in it. CC actions (Clear / Flag / Reject) maintain the annotation but do NOT gate admission to the Volunteers team — admission is automatic once profile + consents are complete.
- The **Statutes** page (`/Legal`) is a separate, anonymous read of the association's statutes pulled directly from GitHub by `LegalDocumentService` (with in-memory caching) — it does not go through the `consent_legal_documents` table.

## Data Model

### LegalDocument

**Table:** `consent_legal_documents`

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

Aggregate-local nav `LegalDocument.Versions` kept. `LegalDocument` carries `TeamId` only, with no navigation; team names are stitched in memory through `ITeamService.GetTeamsWithParentsAsync` (`LegalDocumentSyncService` onto its DTOs, `ConsentService.GetConsentDashboardAsync` when grouping dashboard rows on the `TeamId`/`TeamName` pair).

**Cross-domain FK:** `TeamId` → Teams section, FK only.

### DocumentVersion

**Table:** `consent_document_versions`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| LegalDocumentId | Guid | FK → `consent_legal_documents` |
| VersionNumber | string (50) | Display label |
| CommitSha | string (40) | |
| Content | jsonb | `Dictionary<string, string>` keyed by language code; `"es"` is canonical/legally binding; equality and hashing ignore insertion order |
| EffectiveFrom | Instant | |
| RequiresReConsent | bool | |
| CreatedAt | Instant | |
| ChangesSummary | string? (2000) | Imported GitHub commit first lines are capped at 500 UTF-16 units without splitting a surrogate pair |

Aggregate-local nav `DocumentVersion.LegalDocument` kept. Aggregate-local nav `DocumentVersion.ConsentRecords` declared on the entity and configured in `DocumentVersionConfiguration`; not currently walked by the service layer.

### ConsentRecord

Append-only per design-rules.md, *Append-only entities*. **DB triggers** (`prevent_consent_record_update` / `prevent_consent_record_delete`, both calling `prevent_consent_record_modification()`) raise an exception on any UPDATE or DELETE against `consent_records`; only INSERT is allowed, to maintain GDPR audit-trail integrity. `IConsentRepository` exposes only `Add`/`Get` methods to match.

**Table:** `consent_records`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| UserId | Guid | FK → Users section |
| DocumentVersionId | Guid | FK → `consent_document_versions` |
| ConsentedAt | Instant | |
| IpAddress | string (45) | IPv6-capable; service passes value through unchanged |
| UserAgent | string (1024) | Service caps at 500 UTF-16 units without splitting a surrogate pair |
| ContentHash | string (64) | SHA-256 hex of canonical Spanish content at consent time |
| ExplicitConsent | bool | Always true for valid records |

`ConsentService.SubmitConsentAsync` refuses unchecked consent before writing any record or recording consent metrics. The document remains signable on a later explicit submission; UI checkbox guards are an additional check. Every refused submission shows the member a message, and once a record is saved a failed report to Onboarding is logged, never shown as a failed signature.

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

- **Review ownership.** Reads and writes use only `consent_reviews`; a member without a row has no review yet. Production `GET https://humans.nobodies.team/api/data-migrations` reported `{"key":"consent.profile-review","pending":0,"done":true}` on 2026-11-03. The copy, legacy fallback and review fields on `ProfileInfo` are retired; Users' legacy review columns are dropped. The copy also recorded legacy rejections in `users.State`, so `UserInfo.IsRejected` and later state transitions no longer consult profile columns.
- **Merge.** No `IUserMerge`: rows stay keyed to the id they were written for, and reads resolve a merged-away id forward via `GetUserInfoAsync`, so the survivor's own review stands — as when these were profile columns the profile fold never moved.
- **The rejection state lives in Users.** Reject writes the record here (`IConsentWrite.RecordRejectionAsync`) and the state there (`IUserWrite.SetRejectedAsync`); every gate reads `UserInfo.IsRejected` / the stored `UserState.Rejected` or `ConsentReviewInfo.IsRejected`.
- **GDPR.** Export slice `ConsentReview` (the rows of every id in the member's merge chain). Erasure keeps status, dates, approval and reviewer ids (Art. 17(3)(e) — how the association admitted or rejected the member) and erases the notes and the rejection reason.

## Routing

Five controllers serve this section.

| Controller | Route prefix | Auth |
|------------|-------------|------|
| `LegalController` | `/Legal/{slug?}` | `[AllowAnonymous]` — Statutes page only |
| `ConsentController` | `/Consent` (conventional) | `[Authorize]` |
| `MyDataController` | `/Consent/MyData` | `[Authorize]` |
| `ConsentReviewQueueController` | `/Consent/ReviewQueue` | `PolicyNames.ReviewQueueAccess`; every POST also `PolicyNames.ConsentCoordinatorBoardOrAdmin` |
| `AdminLegalDocumentsController` | `/Legal/Admin/Documents` | `[Authorize(Policy = PolicyNames.BoardOrAdmin)]` |

**Consent routes:**

| Route | Method | Action |
|-------|--------|--------|
| `/Consent` | GET | Dashboard (team-grouped document status) |
| `/Consent/Review?id={versionId}` | GET | Document review before consent |
| `/Consent/Submit` | POST | Record consent; lands on the posted `ReturnUrl` when it is local (the onboarding widget's consent step), else `/Consent` |
| `/Consent/MyData` | GET | My Data page shell: Gdpr's export/deletion actions, the pending-deletion status (read from `UserInfo`) and the member's signed documents (consent history). Carries `[AllowBeforeActiveMembership]` so non-Active members reach it |

**Review queue routes** (the Consent Coordinator's desk; the policy is Shell's, in `AuthorizationPolicyExtensions`):

| Route | Method | Action |
|-------|--------|--------|
| `/Consent/ReviewQueue` | GET | Pending and flagged reviews |
| `/Consent/ReviewQueue/{userId}` | GET | Detail. A merged-away id redirects to the survivor's route, so the forms post the id the page shows |
| `/Consent/ReviewQueue/{userId}/Clear` | POST | Clear |
| `/Consent/ReviewQueue/BulkClear` | POST | Bulk clear; an aborted request propagates cancellation rather than showing a failure toast |
| `/Consent/ReviewQueue/{userId}/Flag` | POST | Flag |
| `/Consent/ReviewQueue/{userId}/Reject` | POST | Reject, through `IOnboardingIntake.RejectSignupAsync` |

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
| ConsentCoordinator, ShiftsAdmin, Board, Admin | Read access to the review queue at `/Consent/ReviewQueue` (`PolicyNames.ReviewQueueAccess`) |
| ConsentCoordinator, Board, Admin | Clear, Flag, or Reject consent checks (`PolicyNames.ConsentCoordinatorBoardOrAdmin`) |
| Board, Admin | Manage legal documents and document versions at `/Legal/Admin/Documents` (`PolicyNames.BoardOrAdmin`): create, edit, archive, trigger manual sync, edit version summaries |

## Invariants

- A replacement consent-snapshot reload cannot publish across an intervening cache mutation. The affected alias is evicted for source retry, so an older reload cannot overwrite a newer signature refresh or restore an evicted snapshot.

- Review notes and rejection reasons are limited to 4,000 Unicode characters before any review write or audit. Oversized text that would be written returns `ReviewTextTooLong`; existing rejection preconditions retain precedence, and valid text is stored without truncation. The operator queue shows the limit and specific refusal feedback.


- Commit-summary fetch failures remain a null fallback: GitHub API failures log Warning with status only, without raw error bodies or exception stacks; unexpected failures log Error with diagnostics. Caller cancellation still propagates.

- Consent review detail and submission each resolve the person once: profile/name checks and prior-signature lookup use that same snapshot’s complete current/merged ID chain. Other consent-chain reads retain their standalone resolution.

- `ConsentService.GetRequiredConsentRowsForUserAsync` owns required-document row shaping; the cache delegates to it. It selects each document's latest effective version, omits future-only documents, and returns unsigned rows first, then ordinal title order.

- The admin GitHub-folder preview renders pasted URL parts as text. It preserves the full URL until the server validates the configured repository and branch and normalizes the folder path.
- The admin legal-document controller orders the document list by team name, then document name, using ordinal comparison. The service returns document data without display ordering.

- Legal-document create, edit, archive, manual sync and version-summary actions resolve their actor through Users before mutation. Merged sessions use the surviving account for audit attribution; missing accounts receive Unauthorized. Account lookup uses request cancellation, while document writes and sync retain their non-cancellable mutation boundary.

- Consent submission and review actions forward request cancellation through their pre-write account reads. Unchecked-consent redisplay and invalid legal-document forms also cancel detail/team reads. Cancellation propagates before mutation; consent writes, onboarding reports and review mutation tokens keep their existing boundaries.

- Legal-document forms require explicit required/active flags and grace-period days; omission redisplays field errors before any write and cannot reactivate a document, change its consent requirement or reset its grace period. Explicit false flags remain valid.

- Legal-document creation and edits refuse empty or nonexistent owning teams with 404 before persistence, initial sync, cache invalidation or success audit. The service checks the team through `ITeamService`; existing inactive teams remain valid owners.

- Manual version summaries are optional and trimmed. Summaries over 2000 characters are refused before persistence, cache invalidation or success audit; the admin editor shows the refusal. Blank summaries clear the existing value.

- Canonical Spanish content must be non-blank. Sync refuses an empty canonical file before
  publishing a version or changing its SHA. Review treats missing/blank canonical content as
  unavailable, and submission records no consent or metrics for it (`ContentUnavailable`).
  The consent page uses its existing localized failure message; onboarding's unavailable-document
  path handles an unavailable review. Hashes always cover the exact canonical text.

- The consent dashboard renders section service DTOs directly, sorting pending teams/documents before signed ones and then by ordinal name. It retains UTC date display and the ten most recent history rows; the controller does not copy DTOs into duplicate row models.

- The member consent dashboard and review pages localize breadcrumb navigation labels in all supported cultures. Consent resources consistently use `Consent_`, including the shared review body the onboarding widget's signing step renders and the empty-state message reused by Governance.
- Anonymous Legal pages use the existing localized content-unavailable message when GitHub content is absent.
- The shared document partial renders the supplied localized empty message for zero translations as well as empty translation text; unavailable Legal content does not leave a blank card.
- Legal and consent document tabs retain native language names and localize the canonical Spanish legal marker in all supported cultures.
- Legal content reads retain caller cancellation through GitHub reads, including anonymous Legal and Governance pages. Caller-cancelled reads propagate without caching unavailable content; already-cancelled callers also stop on cache hits. A fetch timeout without caller cancellation degrades to unavailable content like any other GitHub failure.

- Consent dashboard and review reads carry request cancellation through viewer resolution and dashboard/document reads. Submit and its invalid-form redisplay retain their existing mutation boundaries.
- Legal-document admin list, create and edit GETs retain request cancellation through document reads and the active-team picker. POST redisplay reads keep their mutation boundary.

- Consent records are immutable. Database triggers prevent UPDATE and DELETE operations on `consent_records`. Only INSERT is allowed to maintain GDPR audit trail integrity (`design-rules.md`, *Append-only entities*).
- Required-version snapshots for all documents and for a single team share the same latest-effective-version selection in both the inner service and its cache decorator; documents without an effective version are omitted. The cache evaluates effective dates at read time, so a future version becomes current without a cache reload.
- Legal documents can be global (required of all humans) or team-scoped (required when joining a specific team).
- Sync emails consolidate only outstanding required documents belonging to each recipient’s teams. Optional updates and required updates already signed by that recipient do not trigger a consent email. Emails use the recipient’s supported saved language, with English fallback for missing or unsupported preferences.
- When all required global documents have active consent, the human's consent check status transitions from unset to Pending.
- Legal documents are synced from a GitHub repository by a background job.
- Synced version content is fetched by each file’s immutable blob SHA, after resolving the latest commit changing that path on the configured branch and pinning its metadata read to that commit. The canonical Spanish content and version SHA come from the same fetch, reused without a second branch read. Change summaries use that canonical file’s commit reference; the blob SHA remains the version identity.
- GitHub document reads use the configured `GitHub:Branch`, including both directory discovery and translated file content for the anonymous `/Legal` pages.
- When a new document version is published, existing consents for the old version become stale and re-consent is required.
- Internal `LegalDocumentPublication` owns single-document GitHub sync, version persistence, cache eviction, publication audit and localized fan-out. `LegalDocumentSyncService` retains document administration, read projections and batch error handling; the runner retains durable re-consent batching. Publication still evicts before audit and fan-out, and unchanged content only touches its sync timestamp. Job attribution and cancellation boundaries are preserved; no DI registration or public contract is added.
- Per-user reads on `consent_records` chain-follow merge tombstones via the resolved record's `UserInfo.AllUserIds` so consents signed under a now-merged source id surface for the fold target. Consent records stay at source after merge, DB triggers (`prevent_consent_record_update`, `prevent_consent_record_delete`) make any rewrite physically impossible.
- New signatures use the resolved survivor ID, including when a widget submits an archived account ID. Required-consent checks and post-save suspension-notification cleanup use that same ID; existing immutable signatures keep their original attribution.

- Once a consent record is saved, required-consent checks, suspension-notification resolution, suspension restoration, and inline cache refreshes finish independently of request cancellation. The write evicts every merge-chain cache entry before the required-consent check, so suspension cleanup sees the new signature even when the cache was warm. Validation and the consent write retain the caller token.
- Legal-document creation, edits, archival, version-summary edits and sync writes evict the required-document snapshot even when persistence throws after committing. Known missing-row refusals retain the snapshot; success audit and publication fan-out still require an acknowledged write.
- Every attempted signature insert evicts all resolved merge-chain cache keys, including when persistence throws after committing the append-only record. Later reads then observe the saved signature rather than a stale unsigned state. Pre-write refusals leave the cache untouched. A successful consent submission evicts every affected merge-chain cache key before inline reloads start. A failed reload therefore leaves no alias serving the pre-submit consent set; the failure is logged, the submission still reports success, and later reads reload the committed state.

## Negative Access Rules

- Regular humans **cannot** manage legal documents or document versions.
- ConsentCoordinator **cannot** manage legal documents or versions — they can only review and clear/flag consent checks.
- ShiftsAdmin **cannot** clear, flag, or reject in the review queue: `PolicyNames.ReviewQueueAccess` lets them read it, and every POST requires `PolicyNames.ConsentCoordinatorBoardOrAdmin`.
### ConsentReminder

**Table:** `consent_reminders` — when the re-consent reminder email last went to a member: one row per user id, written by `ConsentReminderService` (the only writer, through `IConsentReminderRepository`) for `SendReConsentReminderJob`.

| Property | Type | Notes |
|----------|------|-------|
| UserId | Guid | PK; FK-only → Users section |
| LastSentAt | Instant | The cooldown anchor; stamped after each successful enqueue |

- **Single owner.** Reminder cooldown and export reads use only `consent_reminders`; the Users column waits for its own drop.
- **Merge.** No `IUserMerge`: rows stay keyed to the id they were written for. Cooldown reads use the latest stamp across the survivor and every merged-away id, keyed by the survivor, so merging accounts cannot restart the reminder cooldown.
- **GDPR.** Export slice `ConsentReminder` (the stamps of every id in the member's merge chain, survivor first). Erasure deletes the member's row.

- No one **can** update or delete consent records. They are permanently immutable.

GitHub document discovery, metadata/raw content, commit-summary and prefix-content reads honour their existing caller token before fetching and while waiting. Cancellation propagates rather than becoming a missing summary or continuing to the next fetch; already-started Octokit requests may finish in the background.

## Triggers

- Scheduled legal sync collects per-document failures while continuing other documents, then drains durable email batches before failing the job. GitHub failures expose only document ID and HTTP status in propagated diagnostics; inner HTML response bodies stay out of job logs. Requested cancellation stops the document loop immediately.
- Full GitHub sync persists a nullable re-consent batch ID with each newly synced required version in the same Consent-owned save. The runner drains these durable batches even when the next sync is unchanged, consolidates outstanding current documents per resolved human, and uses the existing merge-aware consent map. Email recipients use the same `UserInfo.IsActive` predicate as publication notices: profile-less, rejected and tombstone snapshots are excluded; suspended humans with profiles remain eligible to re-consent. A failed team lookup, email preparation/enqueue or completion save leaves the batch pending; other recipients and batches are attempted before the job fails. Stable batch/user delivery IDs reuse successful Email outbox handoffs on retry. Completion clears only that batch. Historical versions remain null without backfill; inactive, optional or superseded versions are retired without email. Admin single-document sync behavior is unchanged.

- When a human signs all required global documents: their consent check status transitions to Pending. `ConsentService.SubmitConsentAsync` fires no per-user team sync — Volunteers admission is reconciled by the scheduled `SystemTeamSyncJob.SyncVolunteersTeamAsync` pass on name + consents (eventually consistent). App access never depended on Volunteers membership.
- When a Consent Coordinator clears a consent check: the review's `IsApproved` is set to true and `Status = Cleared` (`IConsentWrite.RecordConsentCheckAsync`), and Consent writes the `ConsentCheckCleared` audit row itself (callers add none). This is an audit annotation only — clearing provisions no team; Volunteers membership and app access are independent of CC review.
- When a Consent Coordinator flags a consent check: the review's `IsApproved` is set to false and `Status = Flagged`, with a `ConsentCheckFlagged` audit row from the same write. This is an audit annotation only — flagging provisions/deprovisions no team; Volunteers membership and app access are unaffected. `RejectSignupAsync` (the review's `RejectedAt` here, `UserState.Rejected` in Users) is the CC's only actual kick-out lever.
- When a new document version is published: affected humans are notified to re-consent. A background job sends re-consent reminders.
- Re-consent reminder emails render in each recipient’s supported preferred language, with English fallback for blank, malformed or unsupported preferences.
- The reminder job skips a member stamped in `consent_reminders` within `ConsentReminderCooldownDays` and stamps each member it reminds. It attempts every eligible recipient before reporting per-recipient preparation, enqueue, or cooldown-stamp failures. Failed enqueues do not stamp the cooldown; successful reminders retain it. Collected failures still fail the job so Hangfire can retry, skipping recipients already in cooldown. Each member’s delivery ID is anchored to their previous successful-send stamp (or first-send state), so retries after a failed stamp reuse the persisted Email outbox row. A later cooldown cycle uses a new delivery ID.
- Users' `SuspendNonCompliantMembersJob` (04:30, 30 minutes after Consent's reminder job) suspends humans who no longer have valid consents for required documents.

- Legal document names in notices may outgrow the 200-character title limit. Consent sends full copy to Notifications, which bounds stored previews by Unicode character, preserving the full title and existing detail in the body; short notice copy is unchanged.

- Successful document create, settings edit, archive, and version-summary edit write audit events attributed to the Board/Admin actor, after persistence. A created document remains audited if initial GitHub sync fails.
- Re-consent batches skip missing or whitespace-only notification addresses with a Warning; required-document selection and signature checks are unchanged.
- Every persisted new document version writes a publication audit linked to its version ID, before notification fan-out: manual and initial sync name the initiating administrator; scheduled sync names `LegalDocumentSyncService`. Missing rows, failed publication and unchanged-content sync touches do not emit publication events. Signed consent records remain immutable and separate.

## Cross-Section Dependencies

- **Audit actions:** declared on `ConsentAuditActions` (`src/Sections/Humans.Consent/ConsentAuditActions.cs`), one `AuditActionDefinition` each, written through `IAuditLogService`: `ConsentCheckCleared`, `ConsentCheckFlagged`, `LegalDocumentCreated`, `LegalDocumentUpdated`, `LegalDocumentArchived`, `LegalDocumentVersionPublished`, `LegalDocumentVersionSummaryUpdated`.
- **Profiles/Onboarding:** `ConsentService` has no profile dependency and does **not** call into Profile or Onboarding after a consent submit — `ConsentController.Submit` (which the onboarding widget's consent step also posts to) reports the signature with `IOnboardingIntake.ConsentSignedAsync` after `ConsentService.SubmitConsentAsync`, and Onboarding runs the threshold check.
- **Teams:** `ITeamService` — `LegalDocumentSyncService` stitches team names in memory; legal documents are team-scoped (Volunteers team = global).
- **Email:** Consent owns its two templates — `ConsentEmails` (internal) builds each `EmailMessage` from Consent's own `Consent_Email_*` keys in `ConsentResource`, rendered in the recipient's culture via `CultureScope`; `ConsentEmailPreviews` (`IEmailPreviewContributor`, registered in `Section.Register`) lists them at `/Email/EmailPreview`. Email supplies transport only — `IEmailService.SendAsync` (`memory/architecture/email-templates-live-in-sender.md`, peterdrier/Humans#1651).
- **AuditLog:** `IAuditLogService` records legal-document administration and version publication through its existing write contract.
- **Notifications:** `Humans.Notifications.Contracts.INotificationEmitter` (in-app fan-out from `LegalDocumentSyncService`). Consent resolves no notifications itself: once all required consents are complete, `ConsentService` calls `IHumanLifecycleService.RestoreConsentSuspensionAsync`, and Users resolves its own `AccessSuspended` notifications there. Its sources are declared in `ConsentNotificationSources` (`src/Sections/Humans.Consent/ConsentNotificationSources.cs`), one `NotificationSourceDefinition` each: `ReConsentRequired` (actionable, to the active members of the document's team: required doc updated); `LegalDocumentPublished` (actionable, to the active members of the document's team: new required doc published); `ConsentReviewNeeded` (actionable, to consentCoordinator role: retired, purged nightly).
- **Human Lifecycle:** `IHumanLifecycleService.RestoreConsentSuspensionAsync` — `ConsentService` lifts a consent suspension once all required consents are complete; that call also resolves the `AccessSuspended` notification, inside Users. `ConsentService` does not depend on `ISystemTeamSync` — a consent submit provisions no system-team membership; the scheduled `SystemTeamSyncJob` reconciles Volunteers/Coordinators on name + consents.
- **Teams / Auth:** the internal `ConsentCompliance` answers the required-consent questions (has a user signed everything a team requires, signed / required counts, who is past the grace period). It reads team memberships through `ITeamService` to resolve which teams' documents apply to a user (their teams, plus Volunteers, plus Coordinators when they coordinate a user-created team), and `IRoleAssignmentService.GetUserIdsWithActiveAssignmentsAsync` for the population the consent deadline applies to.
- **Governance:** the review queue reads `IGovernanceService` (membership tiers, pending tier applications).
- **Onboarding:** the review queue's Reject calls `IOnboardingIntake.RejectSignupAsync`, which records the rejection here and carries out the state change, team de-provisioning and notice.
- **Gdpr:** the My Data page posts to Gdpr's `MemberDataController` (export, request deletion) and `UserController` (cancel deletion); Consent references Gdpr's project only for those routes and its existing contributor types. Pending-deletion dates come from `UserInfo`.
- **Users/Identity:** `IUserService.GetUserInfoAsync` / `GetUserInfosAsync`, the returned `UserInfo.AllUserIds` chain-follows merge tombstones on every per-user consent read so consents signed under a source id surface for the fold target, and the read resolves a tombstone id forward to its survivor. Consent records are immutable per *Append-only entities* and stay at source. When `LegalDocumentSyncService` publishes a new / re-consent-required version, the in-app notices go to the document's team members (`ITeamService.GetTeamAsync`), resolved through `IUserService.GetUserInfosAsync` and filtered to `IsActive`, the same audience as the email pass (`LegalDocumentSyncRunner`); notices are grouped by supported recipient language in all supported cultures (English fallback), with delivery failures isolated per group.

`IGitHubLegalDocumentConnector` is owned by this section (interface and implementation both `internal` in `Humans.Consent.Services`); not a cross-section dependency.

## Architecture

A delayed per-key or bulk consent load cannot republish its old result after an intervening cache eviction or refresh.

**Owning services:** `LegalDocumentService` (Statutes page), `LegalDocumentSyncService` (document-side — sole writer for `consent_legal_documents`/`consent_document_versions`, owning both the admin write surface `IAdminLegalDocumentService` and the GitHub-sync write surface `ILegalDocumentSyncManagement`; nobodies-collective/Humans#751), `ConsentService` (consent-side), `LegalDocumentSyncRunner` (the GitHub sync + re-consent fan-out body), `ConsentReviewQueueService` (the Coordinator's review queue: list, detail, clear, bulk clear, flag) — all in `Humans.Consent.Services`, `internal sealed`. `SyncLegalDocumentsJob` (`Humans.Consent/Jobs/`) is `internal sealed` too: the section registers and schedules it in `Section.cs`.
**Owned tables:** `consent_legal_documents`, `consent_document_versions`, `consent_records`, `consent_reviews`, `consent_reminders`
**Status:** Own project — `src/Sections/Humans.Consent` + `src/Sections/Humans.Consent/Contracts`. Owns `LegalDbContext` and its migrations; entities, repositories, services, controllers, views and `ConsentResource` are all internal to the assembly. The context keeps the name `LegalDbContext` and the `legal_*` table names.

- Services live in `Humans.Consent/Services/` and take no `DbContext`, `IDbContextFactory<>` or store type in their constructors.
- `ILegalDocumentRepository` (impl `LegalDocumentRepository` in `Humans.Consent/Data/`) is the only code path that touches `consent_legal_documents` and `consent_document_versions` via `DbContext`.
- `IConsentRepository` (impl `ConsentRepository` in `Humans.Consent/Data/`) is the only code path that touches `consent_records` via `DbContext`. Exposes `AddAsync` and `GetXxxAsync` only — no `UpdateAsync`/`DeleteAsync`.
- `IConsentReviewRepository` (impl `ConsentReviewRepository`) is the only code path that touches `consent_reviews`.
- `IConsentReminderRepository` (impl `ConsentReminderRepository`) is the only code path that touches `consent_reminders`.
- Internal `LegalDocumentReader` owns the eight document/version snapshot and admin reads, their current-effective-version selector, and team-name enrichment. `LegalDocumentSyncService` delegates these existing entry points and retains mutations, sync ordering and writer-side cache eviction. Repository and clock read order, future-version exclusion, null-versus-empty team filters, content copying and cancellation are unchanged. No public contract or DI registration is added.
- **Caching** has two layers:
  - **Global** — `CachingLegalDocumentSyncService` (Singleton in `Humans.Consent.Services`) wraps `LegalDocumentSyncService` (inner, keyed Scoped; also the sole writer, implementing both `ILegalDocumentSyncManagement` and `IAdminLegalDocumentService`). Holds the active+required document set as `ActiveRequiredLegalDocumentSnapshot[]` keyed by document id, plus a version-id → document-id index. Serves `GetActiveRequiredDocumentsForTeamsAsync`, `GetRequiredDocumentVersionsForTeamAsync`, `GetRequiredVersionsAsync`, `GetVersionByIdAsync` from cache. `LegalDocumentSyncService` calls `CachingLegalDocumentSyncService.InvalidateAll()` directly after each successful or uncertain repository write (before the publish notification fan-out and the next document of a full sync, so the eviction stays in the writer rather than the decorator's pass-through) (admin create/update/archive/version-summary, GitHub-sync version add/touch) — there is no SaveChanges interceptor (nobodies-collective/Humans#751). Eager warmup at startup: the decorator inherits `TrackedCache<Guid, ActiveRequiredLegalDocumentSnapshot>` with `warmOnStartup: true`, so its own `IHostedService.StartAsync` drives `WarmAllAsync` at boot (non-fatal); no separate warmup hosted service. Warmup reads the inner service’s enriched `GetActiveRequiredDocumentsForTeamsAsync(null)` snapshot; only the inner service resolves team names via `ITeamService.GetTeamsWithParentsAsync`. Null selects every team, while an empty collection selects none. The decorator retains the enriched snapshots without sorting/copying their version lists and builds its version index without resolving another section’s services. Snapshot consumers share `CurrentVersionAt(now)`: the latest version already effective, independent of list order; future-only documents yield none. Entity-based required-version reads reuse their existing current-version selector.
  - **Per-user** — `CachingConsentService` (Singleton in `Humans.Consent.Services`) wraps `ConsentService` (inner, keyed Scoped). Holds `UserConsentInfo` (the user's explicitly consented version-id set, with the merge source-id chain unioned at warm time) keyed by user id. Serves `GetConsentedVersionIdsAsync` and `GetConsentMapForUsersAsync` from cache. Required-consent row composition delegates to the inner service, which owns the legal-document and clock reads. Lazy per-user warm. **Synchronous refresh on `SubmitConsentAsync`**: the decorator override refreshes the user (and any merged-source-id tombstones) via `ReplaceAsync` inline before returning, so the controller's next-page consent-banner check observes the fresh state. Each entry carries the merge-chain ids it was built over; the inner service supplies current chains through its internal `GetUserIdChainsAsync` bulk read. The decorator resolves no Users or legal-document service. A read whose current list differs reloads it, so after an account merge tombstones the source, both the survivor's and the source id's entries rebuild against the new chain on their next read, with no signal from Users.
  - `LegalDocumentService` (Statutes page) keeps its own `IMemoryCache` for the GitHub-fetched anonymous statutes content (zero DB access, pure I/O cache).
  - Architecture tests pin the consent decorator's shape: `ConsentArchitectureTests.{CachingConsentService_DeclaresSubmitConsentAsync, IConsentManagement_And_IConsentService_ResolveToSameSingleton}`.
- Pending-document name reads return distinct names without display ordering. Scanner’s card controller and Agent’s prompt assembler retain ordinal alphabetical presentation; required-consent row ordering still drives Onboarding’s next-document selection.
- **Public contract.** `IConsentService` (12 methods: `GetConsentedVersionIdsAsync`, `GetConsentMapForUsersAsync`, `HasAllRequiredConsentsForTeamAsync`, `GetUsersWithAllRequiredConsentsForTeamAsync`, `GetConsentProgressAsync`, `GetUsersPastConsentDeadlineAsync`, `GetRequiredConsentRowsForUserAsync`, `GetPendingDocumentNamesAsync`, `GetConsentRecordCountAsync`, `GetConsentReviewDetailAsync`, `GetConsentReviewAsync`, `GetConsentReviewsAsync`) is the public contract — only Consent projections, no EF entities. The two review reads pass through `CachingConsentService` uncached. `IConsentWrite` (`RecordConsentCheckAsync`, `SetConsentCheckPendingAsync`, `RecordRejectionAsync`) is the review's write, for this section's review queue and Onboarding's reject and review threshold; `ConsentReviewInfo`, `ConsentCheckStatus` and the queue predicates `ConsentReviewPredicates.NeedsConsentReview` / `IsConsentCheckFlagged` sit beside it. `IConsentSubmission` carries the one write that leaves the section (`SubmitConsentAsync`), for `DevPersonaSeeder`. Both sit on `Humans.Consent.Contracts` alongside `ILegalDocumentService` (statutes), `ILegalDocumentSyncService` (one member, `GetRequiredDocumentVersionsForTeamAsync`, for Governance's membership calculator; the reminder job and metrics snapshot use the internal `ILegalDocumentSyncManagement`). `ConsentSigningStepViewComponent` is the signing step a host page embeds (the onboarding widget): it renders the document, the progress line and the form with Consent's own strings, and the form posts to `/Consent/Submit` with the host's return URL. The internal `IConsentManagement : IConsentService, IConsentSubmission` adds `GetConsentDashboardAsync` and the decorator’s live merge-chain lookup; the admin legal-document surface, the GitHub connector, the sync runner (`ILegalDocumentSyncRunner`, the body of `SyncLegalDocumentsJob`) and the sync service's write half never leave.
- **Aggregate-local navs still declared (not cross-section):**
  - `ConsentRecord.DocumentVersion` — walked by `ConsentRepository.GetAllForUserIdsAsync` (`.ThenInclude(v => v.LegalDocument)`) to surface document name + version on the consent-history view. This is aggregate-local for `consent_records` → `consent_document_versions` → `consent_legal_documents`; not a cross-section nav.
  - `DocumentVersion.ConsentRecords` — declared and configured (`DocumentVersionConfiguration`); not navigated by any current service path.
- **Cross-section calls:** `ITeamService`, `IRoleAssignmentService`, `INotificationEmitter`, `IHumanLifecycleService`, `IUserService`.
- **User parts.** `ConsentReviewCardViewComponent` on `UserPartSlots.AdminDetail` (English, admin view mode only: the consent-check badge and, for a Rejected account, the reason, time and reviewer) and `RejectionReasonViewComponent` on `UserPartSlots.AccountStatus` (Gdpr's status wall, Self view mode, `Consent_AccountStatus_ReasonLabel` in all supported cultures).
- **Architecture tests:** `tests/Humans.Consent.Tests/Architecture/LegalArchitectureTests.cs` (Octokit containment — the sync service must not reference Octokit), `tests/Humans.Consent.Tests/Architecture/ConsentArchitectureTests.cs` (ConsentService, IConsentRepository append-only shape). Page rendering — the G5 template step 12 check — is `tests/Humans.Integration.Tests/Controllers/ConsentPageRenderTests.cs`.

### Touch-and-clean guidance

- `DocumentVersion.ConsentRecords` — declared but not navigated by any service, view, or test. Stripping it is **not** doc-only: `DocumentVersionConfiguration` is the sole declaration of the `consent_records` → `consent_document_versions` FK and its `OnDelete(Restrict)`, so the relationship must be re-expressed from the `ConsentRecord.DocumentVersion` side and a `dotnet ef migrations add` must produce an empty `Up()` to prove the schema is unchanged (see Users' #635 nav-strip precedent).

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

Internal `LegalDocumentAdministration` owns GitHub-folder normalization, create-with-initial-sync, edit/archive and version-summary updates. `LegalDocumentSyncService` retains its entry points; single-document lookup belongs to the existing publication owner and is shared by direct and initial sync. Team guards, option snapshot, regex behavior, refusals, audit/fan-out order, cache invalidation after uncertain writes and cancellation are unchanged. Bulk publication and document reads retain their owners. No public contract or DI registration is added.

Internal `ConsentReader` owns dashboard/review composition, consent counts and version sets, bulk merge-chain/maps, required-document rows and pending document names. It reuses the existing repositories, legal-document service, Users contract, membership-calculator resolution, clock and logger. The contributor reuses its chain lookup. Archived-id resolution, overlapping-input handling, canonical Spanish review refusal, effective-version selection, ordering and cancellation are unchanged. `ConsentService` retains signing, cache eviction, post-commit suspension cleanup, review forwarding and GDPR retention; no contract, DI registration or cache topology changes.
