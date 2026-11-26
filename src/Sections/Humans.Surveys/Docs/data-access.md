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
pattern). The section's services are **Scoped** with no caching decorator (per the spec:
response data is write-heavy and append-only; no hot read path merits a
`TrackedCache` at our small scale). There is no public `ISurveyService` contract (the internal one remains): it shipped
empty and was deleted. The public service contract is `ISurveyAnalysisRead`, consumed by Backdoor's
machine API and Workgroups' linked-survey reads. The section's own
`Jobs/SendSurveyReminderJob` uses internal `ISurveyInvitations`. Everything else — authoring, sending, the wizard, submission —
has no caller outside Surveys.

### SurveyService (Scoped — `ISurveyService`, `IUserDataContributor`, `IUserMerge`)

Authoring, translation pre-fill, approval, open/close, admin lists, official links, GDPR and
merge. Repository: `ISurveyRepository`.

| Table | R/W |
|-------|-----|
| surveys | R/W |
| survey_questions | R/W |
| survey_question_options | R/W |
| survey_invitations | R/W |
| survey_responses | R/W |
| survey_answers | R/W |

Direct cross-section calls use `IUserService`, `IGoogleTranslationService`, `IAuditLogService` and
`IFileStorage` (Information-block images under `uploads/surveys/`). The scoped `SurveyAudience`
supplies audience and access predicates; approval sends through `ISurveyInvitations`.

### SurveyResponseService (Scoped — `ISurveyResponses`)

Answering: invitation and public starts, tracked drafts, the wizard, submission. The only writer of
`survey_responses` and `survey_answers`; marks `survey_invitations` started and completed. Holds the
static response-finalization lock. Reads definitions through `ISurveyService.GetForEditAsync` and
eligibility through `SurveyAudience`; uses `ISurveyInviteTokenProvider`, `IUserService` and `IClock`.
No audit dependency: individual responses are never audited.

### SurveyResultsService (Scoped — `ISurveyResults`, `ISurveyAnalysisRead`)

Read-only analysis: survey summaries, the flattened question graph, results, scoped results and
the response export. Implements `ISurveyAnalysisRead` — Backdoor's and Workgroups' machine-readable
surface (nobodies-collective/Humans#1128). Reads definitions through `ISurveyService.GetForEditAsync`;
uses `IUserService` (respondent names for Identified rows) and `IAuditLogService` (export audit).

Implements `IUserDataContributor` (three GDPR export slices:
`SurveyService.SurveyResponses` — identified responses only; anonymous
and CompletionTracked rows carry no `UserId` and are excluded —
`SurveyService.AuthoredSurveys`, the surveys the person wrote, and
`SurveyService.SurveyInvitations`, the invitation ledger, which is the
only record of someone who was only invited or answered CompletionTracked).
Implements
`IUserMerge`: authorship follows the surviving account. No `IMemoryCache`.

A `LoggedInSince` audience type (`surveys.AudienceLoggedInSince` cutoff
column) resolves from the cached `UserInfo.LastSignInAt` via the existing
`IUserService` fan-out.

### SurveyAudience (Scoped)

No repository or table access. Owns the six audience predicates and Asociado eligibility using
`ITeamService`, `IUserService`, `IGovernanceService`, `ITicketService` and `IShiftView`.
The same scoped instance serves survey access/approval checks and invitation delivery.

### SurveyInvitationDelivery (Scoped — `ISurveyInvitations`)

Reuses the former reminder sender for audience preview, initial invitations, delivery-status reads
and daily reminders. Reads `surveys` and `survey_invitations` through `ISurveyRepository` and
creates invitation rows before delivery and stamps successful sends/reminders after Email outbox acceptance. Uses `SurveyAudience`, `IUserService`
(canonical name, culture and email together), `SurveysEmails`, `ISurveyInviteTokenProvider`,
`IEmailService`, `IAuditLogService` and `IClock`. The reminder job and admin controller use the same scoped
`ISurveyInvitations` registration.
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