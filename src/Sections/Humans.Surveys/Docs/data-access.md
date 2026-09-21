# Surveys — Data Access

## Surveys

Project: `src/Sections/Humans.Surveys`; services under `Services/`,
repository under `Data/`. **DbContext:**
`SurveysDbContext`.
`SurveyRepository` injects `IDbContextFactory<SurveysDbContext>` directly.
Owns `surveys`,
`survey_questions`, `survey_question_options`, `survey_invitations`,
`survey_responses`, `survey_answers`. GDPR-compliant first-party survey
platform with authoring, invite/reminder dispatch, wizard flow, results
aggregation, and full GDPR Article 15 export of identified responses and
authored surveys.

`SurveyRepository` is registered as a **Singleton** (uses `IDbContextFactory`
pattern). `SurveyService` is **Scoped** with no caching decorator (per the spec:
response data is write-heavy and append-only; no hot read path merits a
`TrackedCache` at our small scale). There is no `ISurveyServiceRead`: it shipped
empty and was deleted. The public contracts that leave `Contracts/` are the
single-member `ISurveyReminderSender`, which the section's own
`Jobs/SendSurveyReminderJob` calls, and `ISurveyAnalysisRead`, which Backdoor's
machine API reads. Everything else — authoring, sending, the wizard, submission —
has no caller outside Surveys.

### SurveyService (Scoped — `ISurveyService`, `ISurveyReminderSender`, `IUserDataContributor`, `IUserMerge`)

`ISurveyService` also carries `ISurveyAnalysisRead` — Backdoor's read-only
machine-API surface (survey list, one survey's question graph, the raw
per-response export, per-question aggregates; nobodies-collective/Humans#1128).

Repository: `ISurveyRepository`.

| Table | R/W |
|-------|-----|
| surveys | R/W |
| survey_questions | R/W |
| survey_question_options | R/W |
| survey_invitations | R/W |
| survey_responses | R/W |
| survey_answers | R/W |

Cross-section calls via `ITeamServiceRead` (audience resolution — team
members for `SurveyAudienceType.Team`), `IUserServiceRead` (active-member
enumeration, display-name stitching in results / export), `ITicketServiceRead`
(audience resolution — current-event ticket holders for
`SurveyAudienceType.TicketHolders`), `IShiftView` (audience resolution —
shift participants for `SurveyAudienceType.ShiftParticipants`),
`IUserEmailService` (notification email per invitee), `IEmailService` (outbox
enqueue), the section's own `SurveysEmails` builder (invite and reminder templates),
`ISurveyInviteTokenProvider` (section-local, data-protection invite tokens),
`IGoogleTranslationService` (Cloud Translation pre-fill for admin translation
helper), `IAuditLogService`, `IFileStorage` (Information-block images under
`uploads/surveys/`).

Implements `IUserDataContributor` (three GDPR export slices:
`SurveyService.SurveyResponses` — identified responses only; anonymous
and CompletionTracked rows carry no `UserId` and are excluded —
`SurveyService.AuthoredSurveys`, the surveys the person wrote, and
`SurveyService.SurveyInvitations`, the invitation ledger, which is the
only record of someone who was only invited or answered CompletionTracked).
Implements
`IUserMerge`: authorship follows the surviving account. No `IMemoryCache`.

A `LoggedInSince` audience type (`surveys.AudienceLoggedInSince` cutoff
column) resolves from the cached `UserInfo.LastLoginAt` via the existing
`IUserServiceRead` fan-out.

### SurveyPreviewEmailService (Scoped, `Humans.Surveys.Services`)

No repository, no `IMemoryCache`. Orchestrator (`IOrchestrator`) — sends a
side-effect-free survey invitation preview to the requesting Board/Admin
user, reusing the production invitation template/transport but creating no
invitation, response, or funnel row. Calls `ISurveyService` (own section,
for the survey content), `IUserEmailService` / `IUserServiceRead` (Users),
`IEmailService` / `IEmailPreviewServiceRead` (Email — both via public service
interfaces) plus the section's own `SurveysEmails`, and `SurveyPreviewTokenProvider`
(local, HMAC preview tokens).

### SurveyBranchingEvaluator / SurveyWizardFlow

Pure static helpers — no DI dependencies, no DB access. `SurveyBranchingEvaluator`
validates and evaluates `ShowIf` branching conditions; `SurveyWizardFlow` drives
the multi-page wizard navigation (visible-page resolution, required-answer
validation).

### SurveysEmails (Scoped, internal)

No repository. Pure builder — reads `SurveysResource` (via
`IStringLocalizer<SurveysResource>`) and `EmailSettings`, writes nothing.
Returns `EmailMessage` values for `SurveyService` and
`SurveyPreviewEmailService` to pass to `IEmailService.SendAsync`. It owns the
absolute `/Survey/Answer?t=` link and the sanitized-Markdown pass over an
author's custom invitation copy. No DB access, no cache.

### SurveysEmailPreviews (Scoped)

No repository. Read-only gallery contributor (`IEmailPreviewContributor`) —
builds one sample per template via `SurveysEmails` for `/Email/EmailPreview`.
No DB access, no cache.

---
