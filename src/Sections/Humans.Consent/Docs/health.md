# Consent — Health

Target shape derived fresh each section-doctor run (Phase 3c), before scans. History at the
bottom.

## What the section does

Keeps the collective's legal paperwork honest and records who has agreed to what. It holds the
legal documents members must agree to, pulls their text from a GitHub repository when the legal
team publishes a change (multi-language, Spanish legally binding), shows each member what they
still owe, records each agreement as a permanent tamper-proof entry, and reminds the people
affected when a document changes. Admins manage the document set from a GUI. Anyone, signed in
or not, can read the association's statutes. It also keeps the Consent Coordinator's verdict on
each new member (cleared, flagged, rejected with a reason) and shows the member their rejection
reason, while the review queue itself lives in Onboarding.

## The shapes

| Shape | Question it answers | Surface |
|---|---|---|
| Per-user signed-state | "Which versions has this user signed?" | `IConsentService`: `GetConsentedVersionIdsAsync`, `GetConsentMapForUsersAsync`, `GetRequiredConsentRowsForUserAsync`, `GetPendingDocumentNamesAsync`, `GetConsentRecordCountAsync`: projections of one fact table, the first two per-user cached |
| Review & sign | "Show one document; record my agreement" | `GetConsentReviewDetailAsync` + `IConsentSubmission.SubmitConsentAsync`; `/Consent`, `/Consent/Review`, `/Consent/Submit` |
| My data | "What have I signed; is my deletion pending?" | `/Consent/MyData` over the internal dashboard read |
| Required-document set | "What is required, per team, right now?" | `ILegalDocumentSyncService` + internal reads, all views over one cached active+required set |
| Keep documents current | "Has the source changed? Pull it." | the internal sync surface, the sync runner, the sync and reminder Hangfire jobs at 04:00 |
| Admin CRUD | "Create/edit/archive/sync a document" | `IAdminLegalDocumentService`, the routes under `/Legal/Admin/Documents` |
| Public statutes | "Show the published statutes/terms" | `ILegalDocumentService`, `/Legal/{slug?}`, GitHub-direct, no DB |
| Coordinator verdict | "Was this member cleared, flagged or rejected, and why?" | `IConsentService.GetConsentReviewAsync`/`GetConsentReviewsAsync`, `IConsentWrite`, two user-part view components |

Owned tables: `legal_documents`, `document_versions`, `consent_records` (append-only, DB
triggers), `consent_reviews`, `consent_reminders`. Cross-section calls out: Teams (names,
members), Users (info, merge chains, rejected state for the legacy copy), Governance (required
teams, completion), Notifications (fan-out), Email (two templates), AuditLog, HumanLifecycle
(suspension restore). Calls in: Onboarding (review writes, queue reads), Governance (required
versions), Shell's onboarding widget (submit), the Tickets and Development seeders. Config read:
`GitHubSettings`, `EmailSettings` (reminder windows), `Email:DpoAddress`.

## Structure

Three aggregates and one content proxy, each with a single writer:

- **Document side**: `LegalDocumentRepository` (sole EF access to `legal_documents` +
  `document_versions`), `LegalDocumentSyncService` (sole writer; admin and sync surfaces on one
  instance), `CachingLegalDocumentSyncService` (singleton decorator, warm on boot, whole-set
  invalidation).
- **Consent side**: `ConsentRepository` (append-only), `ConsentService` (business rules, GDPR
  contributor), `CachingConsentService` (per-user lazy cache, synchronous refresh on submit).
- **Member-review side**: `ConsentReviewRepository` + `ConsentReviewService` (verdict rows, legacy
  profile fallback until the `consent.profile-review` copy retires), `ConsentReminderRepository` +
  `ConsentReminderService` (the reminder cooldown stamp).
- **Statutes proxy**: `LegalDocumentService`, GitHub to IMemoryCache, no DB.
- Controllers translate only; jobs are schedule + metric + failure boundary over the sync runner
  and the reminder pass. Both jobs are internal: the section registers and schedules them itself.

## Invariants

- `consent_records` is append-only: the repository exposes Add and reads only
  (`Data/IConsentRepository.cs:12`) and DB triggers reject UPDATE and DELETE
  (`Data/Migrations/20260810192942_BaselineLegal.cs:151`).
- Every per-user consent read follows the merge chain: `ConsentService` resolves
  `UserInfo.AllUserIds` before reading (`Services/ConsentService.cs:33`).
- A consent is recorded only with explicit consent (`Services/ConsentService.cs:126`), from a
  profile with all required names (`Services/ConsentService.cs:130`), once per user and version
  (unique index, `Data/ConsentRecordConfiguration.cs:34`), with the user agent cut to 500
  characters (`Services/ConsentService.cs:156`).
- Sync creates a version only when the canonical `es` file's commit changed
  (`Services/LegalDocumentSyncService.cs:535`); a folder with no `es` file refuses to sync
  (`Services/LegalDocumentSyncService.cs:509`); every version after the first requires re-consent
  (`Services/LegalDocumentSyncService.cs:574`).
- Every persisted document write invalidates the global cache before the call returns
  (`Services/LegalDocumentSyncService.cs:591`), and a successful submit refreshes the user's cache
  entry before returning (`Services/CachingConsentService.cs:142`).
- A cleared verdict is refused for a rejected member, and a second rejection is refused
  (`Services/ConsentReviewService.cs:61`, `Services/ConsentReviewService.cs:93`).
- A reminder is not sent to a member stamped within the cooldown
  (`Jobs/SendReConsentReminderJob.cs:65`).
- Auth: `/Consent` any signed-in human (`Controllers/ConsentController.cs:14`); `/Legal`
  anonymous (`Controllers/LegalController.cs:13`); `/Legal/Admin/*` Board or Admin only
  (`Controllers/AdminLegalDocumentsController.cs:13`), so a Consent Coordinator cannot manage
  documents.

## Seams

- **The Consent Coordinator review queue** lives in Onboarding (`/OnboardingReview`); this
  section stores the verdicts and registers the policy that gates the queue.
- **Retiring the legacy review fallback** waits on the `consent.profile-review` data migration in
  production; then `LegacyReview` and the profile review columns go.
- `ILegalDocumentSyncService` cannot fold inward while Governance's `MembershipCalculator`
  injects it.

## Deliberately not done

- No per-document cache keys on the legal cache: the consumed unit is the whole active+required
  set, so invalidation is wholesale.
- No FK constraints or navs across sections (`LegalDocument.TeamId`, `ConsentRecord.UserId` are
  bare Guids).
- No eager warm on the per-user consent cache: the banner workload fills it lazily.
- The statutes page does not go through `legal_documents`: it is a straight GitHub read with its
  own cache.
- Completing consents gates no app access and provisions nothing; the scheduled system-team sync
  reconciles Volunteers membership on name + consents.
- `LegalDocumentService` versus `LegalDocumentSyncService` naming collision is accepted.

## Load-bearing weirdness

- **Keyed-Scoped inner + Singleton decorator** DI for both caches; the inner sync service
  implements two interfaces and `IAdminLegalDocumentService` resolves to the same keyed instance
  so admin and sync writes share one writer (nobodies-collective/Humans#751).
- **`ConsentReviewFormViewModel` sits on the contracts** so Shell's onboarding widget can build it
  and render the section's `_ConsentReviewBody` partial.
- **`_ConsentReviewBody` uses `ResourceManager` directly** to hand per-language checkbox strings to
  inline JS that re-localises when the reader switches language tabs.
- **The post-submit onboarding report is a controller call** (`ConsentController.Submit` to
  `IOnboardingIntake.ConsentSignedAsync`), avoiding a leaf-to-orchestrator arrow.
- **`ConsentService` takes `IServiceProvider`** to resolve `IMembershipCalculator` lazily:
  Governance depends back on Consent, so a constructor arrow would cycle.
- **`RecordConsentCheckAsync` writes its own audit row**; the other two review writes leave audit
  to the caller.
- **The issue queue key is `Legal`**: stored issue rows carry the section's old name.
- **`ConsentResource` must stay `namespace Humans.Consent`** and public (localization manifest
  naming).
- **Reminder at 04:00, suspension (Users) at 04:30**: the half-hour gap is the design.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| section-doctor | 2026-08-31 | First pass: doc truth vs post-G5 code, dead sync surface, notification fan-out delta | peterdrier/Humans#1572 |
| section-doctor | 2026-10-31 | Consent-submit errors shown, member-review lane in the target, sync runner internal | pending |
