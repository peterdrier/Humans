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
aggregation, and full GDPR Article 15 export of identified responses.

`SurveyRepository` is registered as a **Singleton** (uses `IDbContextFactory`
pattern). `SurveyService` is **Scoped** with no caching decorator (per the spec:
response data is write-heavy and append-only; no hot read path merits a
`TrackedCache` at ~500-user scale). There is no `ISurveyServiceRead`: it shipped
empty in v1, no cross-section consumer ever appeared, and it was deleted at G5.
The section's only outbound contract is the single-member
`Humans.Surveys.Contracts.ISurveyReminderSender`.

### SurveyService (Scoped — `ISurveyService`, `IUserDataContributor`)

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
enqueue), `IEmailMessageFactory` (invite and reminder templates),
`ISurveyInviteTokenProvider` (Infrastructure — HMAC invite tokens),
`IGoogleTranslationService` (Cloud Translation pre-fill for admin translation
helper), `IAuditLogService`.

Implements `IUserDataContributor` (GDPR export slice
`GdprExportSections.SurveyResponses` — identified responses only; anonymous
and CompletionTracked rows carry no `UserId` and are excluded). No
`IMemoryCache`.

A `LoggedInSince` audience type (`surveys.AudienceLoggedInSince` cutoff
column) resolves from the cached `UserInfo.LastLoginAt` via the existing
`IUserServiceRead` fan-out.

### SurveyPreviewEmailService (Scoped, `Humans.Surveys.Services`)

No repository, no `IMemoryCache`. Orchestrator (`IOrchestrator`) — sends a
side-effect-free survey invitation preview to the requesting Board/Admin
user, reusing the production invitation template/transport but creating no
invitation, response, or funnel row. Calls `ISurveyService` (own section,
for the survey content), `IUserEmailService` / `IUserServiceRead` (Users),
`IEmailService` / `IEmailMessageFactory` / `IEmailPreviewServiceRead` (Email
— all via public service interfaces), plus `SurveyPreviewTokenProvider`
(local, HMAC preview tokens).

### SurveyBranchingEvaluator / SurveyWizardFlow

Pure static helpers — no DI dependencies, no DB access. `SurveyBranchingEvaluator`
validates and evaluates `ShowIf` branching conditions; `SurveyWizardFlow` drives
the multi-page wizard navigation (visible-page resolution, required-answer
validation).

---


