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
`TrackedCache` at our small scale). There is no public `ISurveyService` contract (the internal one remains): it shipped
empty and was deleted. The public contracts that leave `Contracts/` are the
single-member `ISurveyReminderSender`, which the section's own
`Jobs/SendSurveyReminderJob` calls, and `ISurveyAnalysisRead`, which Backdoor's
machine API reads. Everything else — authoring, sending, the wizard, submission —
has no caller outside Surveys.

### SurveyService (Scoped — `ISurveyService`, `IUserDataContributor`, `IUserMerge`)

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

Direct cross-section calls use `IUserService` (member identity and response/export stitching),
`IGoogleTranslationService`, `IAuditLogService` and `IFileStorage` (Information-block images under
`uploads/surveys/`). The scoped `SurveyAudience` supplies audience and access predicates;
`SurveyInvitationDelivery` owns initial/reminder transport and invitation delivery-status reads.
`ISurveyInviteTokenProvider` remains section-local for answer-link resolution.

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
`IUserService` fan-out.

### SurveyTranslationFiller (constructed by SurveyService, not DI-registered)

No repository, no `IMemoryCache`, no table access. Fills blank localized texts of an in-flight survey edit from
the default culture, one batched `IGoogleTranslationService` call per target culture; authored text is never
overwritten. Returns the updated edit input and the filled count; `SurveyService` persists it.

### SurveyAudience (Scoped)

No repository or table access. Owns the six audience predicates and Asociado eligibility using
`ITeamService`, `IUserService`, `IGovernanceService`, `ITicketService` and `IShiftView`.
The same scoped instance serves survey access/approval checks and invitation delivery.

### SurveyInvitationDelivery (Scoped — `ISurveyReminderSender`)

Reuses the former reminder sender for audience preview, initial invitations, delivery-status reads
and daily reminders. Reads `surveys` and `survey_invitations` through `ISurveyRepository` and
creates invitation rows before delivery and stamps successful sends/reminders after Email outbox acceptance. Uses `SurveyAudience`, `IUserService`
(canonical name, culture and email together), `SurveysEmails`, `ISurveyInviteTokenProvider`,
`IEmailService`, `IAuditLogService` and `IClock`. The existing reminder contract resolves to the
same scoped instance; `SurveyService` forwards its existing internal invitation entry points.
Stable delivery IDs, audit actors, cancellation boundaries and retry behavior are preserved.

### SurveyPreviewEmailService (Scoped, `Humans.Surveys.Services`)

No repository, no `IMemoryCache`. Orchestrator (`IOrchestrator`) — sends a
side-effect-free survey invitation preview to the requesting
user, reusing the production invitation template/transport but creating no
invitation, response, or funnel row. Calls `ISurveyService` (own section,
for the survey content), `IUserService` (Users: canonical name, culture and email),
`IEmailService` / `IEmailPreviewService` (Email — both via public service
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

### SurveyDefinitionReader (internal, owned by SurveyService)

Repository: `ISurveyRepository`. Reads summary counts, official invitation/public
links and complete survey definitions. Reuses the existing invite-token provider
and `SurveyQuestionMapping`; no cross-section dependencies, cache or separate
DI registration. The existing survey service remains the caller-facing contract.
