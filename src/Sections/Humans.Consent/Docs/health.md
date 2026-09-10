# Consent — Health

Target shape derived fresh each section-doctor run (Phase 3c), before scans. History at the
bottom.

## What the section does

Keeps the collective's legal paperwork honest. It holds the set of legal documents members
must agree to, pulls their text from a GitHub repository whenever the legal team publishes a
change (multi-language, Spanish legally binding), shows each member what they still owe,
records each agreement as a permanent tamper-proof entry, and nags the people affected when
a document changes. Admins manage the document set from a GUI. Anyone — signed in or not —
can read the association's statutes.

## The shapes

| Shape | Question it answers | Surface |
|---|---|---|
| Per-user signed-state | "Which versions has this user signed?" | `IConsentServiceRead`: `GetConsentedVersionIdsAsync`, `GetConsentMapForUsersAsync`, `GetRequiredConsentRowsForUserAsync`, `GetPendingDocumentNamesAsync`, `GetConsentRecordCountAsync` — projections of one fact table; the first three per-user cached, the last two pass-through to the inner service |
| Review & sign | "Show one document; record my agreement" | `GetConsentReviewDetailAsync` + `IConsentSubmission.SubmitConsentAsync`; `/Consent` GET, `/Consent/Review` GET, `/Consent/Submit` POST |
| Required-document set | "What is required, per team, right now?" | `ILegalDocumentSyncServiceRead` + internal dashboard/version reads — all views over one cached active+required set |
| Keep documents current | "Has the source changed? Pull it." | internal sync surface (`SyncAllDocumentsAsync`, `SyncDocumentAsync`), `ILegalDocumentSyncRunner` (job body), the sync + reminder Hangfire jobs at 04:00 |
| Admin CRUD | "Create/edit/archive/sync a document" | `IAdminLegalDocumentService`, the routes under `/Legal/Admin/Documents` |
| Public statutes | "Show the published statutes/terms" | `ILegalDocumentService`, `/Legal/{slug?}`, GitHub-direct, no DB |
| Cache signals | "This data changed elsewhere" | `IConsentCacheInvalidator` (account merge), internal `ILegalDocumentCacheInvalidator` (writer self-invalidation) |

Owned tables: `legal_documents`, `document_versions`, `consent_records` (append-only, DB
triggers). Cross-section calls out: Teams (names), Users (info, merge chains, reminder
cooldown), Governance (required teams / completion), Notifications (fan-out, auto-resolve),
Email (re-consent mails), Gdpr (export contributor), HumanLifecycle (suspension restore).
Config read: `GitHubSettings`, `EmailSettings` (reminder windows).

## Structure

The shapes imply exactly two aggregates and one content proxy, each with a single writer:

- **Document side** — `LegalDocumentRepository` (sole EF access to `legal_documents` +
  `document_versions`), `LegalDocumentSyncService` (sole writer; admin + sync surfaces on
  one instance), `CachingLegalDocumentSyncService` (singleton decorator, warm-on-boot,
  whole-set invalidation).
- **Consent side** — `ConsentRepository` (append-only), `ConsentService` (business rules,
  GDPR contributor), `CachingConsentService` (per-user lazy cache, synchronous refresh on
  submit).
- **Statutes proxy** — `LegalDocumentService`: GitHub → IMemoryCache, no DB. Separate on
  purpose; only the name is unfortunate (see weirdness).
- Controllers translate only; jobs are schedule + metric + failure boundary over
  `ILegalDocumentSyncRunner` / the reminder pass.
- Contracts leaf carries only what outside callers use.

Today's layout matches this. The remaining deltas await Peter's call and sit in the
2026-08-31 run file's Needs-Peter list (fan-out scope, nav-property strip, email edge,
contract fold-inward).

## Invariants

- `consent_records` is append-only: repository exposes Add + reads only; DB triggers reject
  UPDATE/DELETE. New state = new row. Records survive account deletion (Art. 17(3)(e)) and
  stay at source after account merge.
- Every per-user consent read chain-follows merge tombstones (`{userId ∪ merged source ids}`).
- A consent is recorded only with `ExplicitConsent` true, from a non-stub profile (verified
  legal name), once per (user, version) — unique index — and carries IP, user-agent
  (truncated to 500), and SHA-256 of the canonical Spanish content.
- Consent completion never gates app access; Volunteers membership is reconciled by the
  scheduled system-team sync on name + consents. Submitting a consent provisions nothing.
- Sync creates a new version only when the canonical `es` file's commit SHA changed; a
  folder without an `es` file refuses to sync. Non-initial versions require re-consent.
- Required-ness is team-scoped; Volunteers-team documents are global. Grace periods are
  data here, enforced in Governance's membership calculator.
- After every persisted document write the global cache is invalidated before the call
  returns; after a successful submit the user's cache entry is refreshed before the
  controller redirects (the next-page banner must not read stale state).
- Auth: `/Consent` any authenticated human; `/Legal` anonymous; `/Legal/Admin/*` Board or
  Admin only; ConsentCoordinator has no document-management rights.

## Seams

- **Consent Coordinator review queue** lives in Onboarding (`/OnboardingReview`), not here;
  this section only feeds it. Docs describe the joint flow — keep the boundary in mind when
  editing either.
- `ILegalDocumentSyncRunner`'s consumers all live in this section, so it could fold inward
  (noted in the Contracts csproj); `ILegalDocumentSyncServiceRead` cannot yet — Governance's
  `MembershipCalculator` injects it. Shrinking contract surface needs Peter.

## Deliberately not done

- No per-document cache keys on the legal cache — the consumed unit is the whole
  active+required set; invalidation is wholesale by design.
- No batching of `GetMergedSourceIdsAsync` in `GetConsentMapForUsersAsync` — declined at
  this scale (TODO(perf) in code).
- No FK constraints or navs across sections (`LegalDocument.TeamId`, `ConsentRecord.UserId`
  are bare Guids) — data-ownership rule, not an omission.
- No eager warm on the per-user consent cache — the banner workload fills it lazily.
- The statutes page does not go through `legal_documents` — it is a straight GitHub read
  with its own 1-hour cache; failures cache empty for 30s.
- `LegalDocumentService` vs `LegalDocumentSyncService` naming collision is accepted — the
  merged writer took the free name (data-access.md explains).

## Load-bearing weirdness

- **Keyed-Scoped inner + Singleton decorator** DI dance for both caches; the inner sync
  service implements two interfaces and `IAdminLegalDocumentService` resolves to the same
  keyed instance so admin and sync writes share one writer (#751).
- **`ConsentReviewFormViewModel` sits on the contracts leaf** so Shell's onboarding widget
  can construct it and render the section's `_ConsentReviewBody` partial — the widget step
  view binds a Shell type the section cannot name.
- **`_ConsentReviewBody` uses `ResourceManager` directly** to hand per-language checkbox
  strings to inline JS that re-localises when the reader switches language tabs.
- **Jobs are `public`** (HUM0034 exception) because Shell names the concrete types at
  registration; `SendReConsentReminderJob` carries `[CrossSectionWrite]` for its cooldown
  stamp on Users.
- **Post-submit threshold check is a controller peer-call** (`ConsentController.Submit` →
  `IOnboardingIntake`), not a service call — avoids the inverted arrow into Onboarding.
- **`ConsentService` takes `IServiceProvider`** to resolve `IMembershipCalculatorRead`
  lazily — Governance depends back on Consent, so the direct constructor arrow would cycle.
- **`ConsentResource` must stay `namespace Humans.Consent`** and public (localization
  manifest naming + boot diagnostic via exported types).
- **Reminder at 04:00, suspension (Users) at 04:30** — the half-hour gap is the design.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| section-doctor | 2026-08-31 | First pass: doc truth vs post-G5 code, dead sync surface, notification fan-out delta | peterdrier/Humans#1572 |
