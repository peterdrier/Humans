<!-- freshness:triggers
  src/Humans.Application/Services/Surveys/**
  src/Humans.Application/Interfaces/Surveys/**
  src/Humans.Domain/Entities/Survey*.cs
  src/Humans.Domain/ValueObjects/LocalizedText.cs
  src/Humans.Domain/ValueObjects/BranchCondition.cs
  src/Humans.Infrastructure/Data/Configurations/Surveys/**
  src/Humans.Infrastructure/Repositories/Surveys/SurveyRepository.cs
  src/Humans.Infrastructure/Jobs/SendSurveyReminderJob.cs
  src/Humans.Web/Controllers/SurveyController.cs
  src/Humans.Web/Controllers/SurveyAdminController.cs
  src/Humans.Web/Controllers/SurveysApiController.cs
-->
<!-- freshness:flag-on-change
  Anonymity encoding (Identified/CompletionTracked/Anonymous), the no-completion-timestamp rule, server-side branching, idempotent invite ledger, and the no-cross-section-nav FK style — review when Survey service/entities/configs/controllers change.
-->

# Survey — Section Invariants

First-party, GDPR-compliant surveys: author typed/branching multi-language surveys, send tokenised email invitations to a resolved audience, collect responses across three anonymity tiers (invite link or public slug), and read results in-app, via CSV/JSON export, and a key-authed analysis API.

## Concepts

- A **Survey** is an authored questionnaire with per-culture title/intro/thank-you (`LocalizedText`), a default culture, a status lifecycle (Draft → Open → Closed), an optional open/close window, an audience, and an optional public slug. It owns an ordered graph of questions.
- A **SurveyQuestion** is one prompt on a page, typed (SingleChoice, MultiChoice, ShortText, LongText, Rating), optionally required, optionally gated by a **`ShowIf` branch condition**. Choice questions own ordered **SurveyQuestionOptions** (stable machine `Value` + `LocalizedText` label).
- A **branch condition** (`BranchCondition` + `BranchClause`, jsonb) is skip-logic: a question is visible only when its clauses (combined `All`/`Any`, operators `Is`/`IsNot`/`Answered`/`NotAnswered` over earlier questions' option values) evaluate true. Evaluated by the pure `SurveyBranchingEvaluator`.
- A **SurveyInvitation** is the per-recipient ledger row for the invited path: one per `(SurveyId, UserId)`. It carries send/reminder funnel state (`SentAt`, `LatestEmailStatus`, `ReminderSentAt`, `Started`, `Completed`) — all flags/timestamps about *participation*, never about *answer content*.
- A **SurveyResponse** is one submitted (or, for Identified, in-progress) answer set, tagged with its **anonymity tier** and **input method** (UserSpecificLink vs Slug). It owns **SurveyAnswers** (selected option values, free text, or rating).
- **Anonymity tiers** (`ResponseAnonymity`): **Identified** (linked to the invitee, resumable, the only personal-data tier), **CompletionTracked** (participation counted, answers unlinkable), **Anonymous** (no trace). See Invariants.
- The **public-slug path** is an anonymous answering route (`/Survey/{slug}`) with no invitation/token; responses are always Anonymous + `InputMethod=Slug`.

## Data Model

### Survey

**Table:** `surveys`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| Title / Intro / ThankYou | LocalizedText | jsonb (culture → text); default `'{}'::jsonb` |
| DefaultCulture | string | max 10; fallback culture for resolution |
| AllowAnonymous | bool | gates the anonymity selector and the public slug |
| Status | SurveyStatus | string-converted; Draft / Open / Closed |
| OpensAt / ClosesAt | Instant? | optional open/close window |
| AudienceType | SurveyAudienceType? | string-converted; null = no audience |
| AudienceTeamId | Guid? | bare FK → Team (when `AudienceType = Team`) — **FK only**, no nav, no cross-section EF FK constraint |
| PublicSlug | string? | max 80; public answering link; requires `AllowAnonymous`; null = invite-only |
| PublicStartedCount | int | slug-path "started" funnel counter (no per-person anchor) |
| CreatedByUserId | Guid | bare FK → User — **FK only**, no nav, resolved via `IUserServiceRead` |
| CreatedAt / UpdatedAt | Instant | |

**Indexes:** `Status`; `PublicSlug` unique (filtered to non-null).

"Which cultures have content" is **derived** from the `LocalizedText` dictionaries — there is no `Cultures` column.

### SurveyQuestion

**Table:** `survey_questions` — aggregate-local to `surveys` (`Survey.Questions ↔ SurveyQuestion.Survey`, Cascade; legal `.Include` inside the repository).

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| SurveyId | Guid | FK → Survey, Cascade |
| PageNumber / Order | int | page sequencing |
| Type | SurveyQuestionType | string-converted |
| Prompt / HelpText / RatingMinLabel / RatingMaxLabel | LocalizedText | jsonb |
| IsRequired | bool | |
| RatingMin / RatingMax | int? | rating-question range |
| ShowIf | BranchCondition? | jsonb skip-logic |

**Index:** `(SurveyId, PageNumber, Order)`.

### SurveyQuestionOption

**Table:** `survey_question_options` — aggregate-local to `survey_questions` (Cascade).

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| QuestionId | Guid | FK → SurveyQuestion, Cascade |
| Order | int | |
| Value | string | max 100; stable machine key (not localised; used as branching/export join key) |
| Label | LocalizedText | jsonb |

### SurveyInvitation

**Table:** `survey_invitations`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| SurveyId | Guid | FK → Survey |
| UserId | Guid | bare FK → User — **FK only**, no nav, no cross-section EF FK constraint |
| SentAt | Instant? | when the invite email was queued |
| LatestEmailStatus | EmailOutboxStatus? | string-converted; `Queued` at enqueue, `Failed` on synchronous send exception |
| ReminderSentAt | Instant? | stamped when the one-time 7-day reminder fires (idempotency anchor) |
| Completed | bool | **flag only — NO completion timestamp** (see Invariants) |
| Started | bool | funnel "started"; set on first advance past intro; **bool only, no timestamp** |
| CreatedAt | Instant | |

**Indexes:** unique `(SurveyId, UserId)` (one ledger row per recipient); `(SurveyId, Completed, SentAt)` (reminder sweep). No `UpdatedAt` column (timing side-channel).

### SurveyResponse

**Table:** `survey_responses`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| SurveyId | Guid | FK → Survey |
| InvitationId | Guid? | set **ONLY** for Identified; intra-section FK → SurveyInvitation, SetNull |
| UserId | Guid? | set **ONLY** for Identified; bare FK → User — **FK only**, no nav, no cross-section EF FK constraint |
| Anonymity | ResponseAnonymity | string-converted |
| InputMethod | SurveyInputMethod | string-converted; UserSpecificLink / Slug |
| Culture | string | max 10; the culture the response was answered in |
| SubmittedAt | Instant? | null = in-progress Identified draft (resumable); set at final submit |

**Indexes:** `SurveyId`; `(SurveyId, UserId)` (resume lookup).

### SurveyAnswer

**Table:** `survey_answers` — aggregate-local to `survey_responses` (Cascade).

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| ResponseId | Guid | FK → SurveyResponse, Cascade |
| QuestionId | Guid | FK → SurveyQuestion, Restrict |
| SelectedOptionValues | List&lt;string&gt; | jsonb; stable option `Value`s |
| TextValue | string? | max 4000 |
| RatingValue | int? | |

### Enums

| Enum | Values |
|------|--------|
| SurveyStatus | Draft, Open, Closed |
| SurveyQuestionType | SingleChoice, MultiChoice, ShortText, LongText, Rating |
| ResponseAnonymity | Identified, CompletionTracked, Anonymous |
| SurveyInputMethod | UserSpecificLink, Slug |
| SurveyAudienceType | Team, AllActiveMembers, TicketHolders, ShiftParticipants |
| BranchCombine | All, Any |
| BranchOperator | Is, IsNot, Answered, NotAnswered |

`LocalizedText` (culture → text) and `BranchCondition`/`BranchClause` are Survey-/Domain-owned value objects in `Humans.Domain/ValueObjects/`, all persisted as jsonb.

## Routing

- **`/Survey/Admin/*`** — `SurveyAdminController` (BoardOrAdmin): index, builder, send, results, CSV/JSON export.
- **`/Survey/Answer?t={token}`** — `SurveyController` invited wizard (token carries identity; never the current principal).
- **`/Survey/{slug}`** — `SurveyController` public anonymous wizard. Literal segments `Admin`/`Answer` are **reserved slugs** and resolve before `{slug}`.
- **`/api/surveys/*`** — `SurveysApiController` (key-authed, read-only).

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| BoardOrAdmin (`PolicyNames.BoardOrAdmin`) | Author surveys (builder), open/close, send invitations, view results + Identified drill-down, export CSV/JSON. |
| Invited member | Answer their invited survey via the tokenised link; choose anonymity tier when `AllowAnonymous`; resume an unfinished Identified draft. Reachable even for non-members (`Survey` is in `MembershipRequiredFilter.ExemptControllers`; answer actions are `[AllowAnonymous]`). |
| Anonymous public visitor | Answer via the public slug — always Anonymous, `InputMethod=Slug`, no identity. |
| API (key auth) | List surveys, get a definition, read responses (`?format=md`/json) and aggregates via `/api/surveys` — read-only; key from `SURVEY_API_KEY` (`SurveyApiKeyAuthFilter`; 503 when unset, 401 when wrong). |

## Invariants

- **Anonymity encoding** is the load-bearing privacy contract:

  | Tier | `Response.UserId` | `Response.InvitationId` | `Invitation.Completed` | Resume? | In GDPR export / drill-down / API identity |
  |------|------|------|------|------|------|
  | **Identified** | invitee id | invitation id | true | yes (draft, `SubmittedAt` null) | yes — only personal-data tier |
  | **CompletionTracked** | null | null | true (**bool only — no timestamp**) | no | no |
  | **Anonymous** | null | null | false (untouched) | no | no |

  `UserId`/`InvitationId` are written on the response **only** for Identified. CompletionTracked flips the invitation's `Completed` flag (known from the wizard token) without persisting any link on the response.
- **`Completed` is a boolean with no timestamp** and `survey_invitations` has no `UpdatedAt`: recording *when* a CompletionTracked invitee finished would correlate (user-linked) with the unattributed response's `SubmittedAt` and re-identify them.
- **Resume is Identified-only.** An in-progress Identified response is a persisted draft (`SubmittedAt is null`), found by `(SurveyId, UserId, SubmittedAt is null)`. CompletionTracked/Anonymous carry no link, are held in session, and **restart** on reopen.
- **Branching is server-side and authoritative.** A null `ShowIf` is visible; hidden questions are never treated as required; at submit the full branching is re-evaluated and answers to hidden questions are **dropped/rejected** (the client cannot smuggle them). Author-save rejects `ShowIf` forward-references (`SurveyBranchingEvaluator.ValidateNoForwardReferences`).
- **Invitation send is idempotent and additive.** Each send resolves the audience, diffs against existing `(SurveyId, UserId)` invitations, and creates+emails only net-new recipients; nobody is double-invited and **sends never revoke**. Requires the survey Open with an audience. Invites are operational (`MessageCategory.System`, always-send) — surveys are never marketing.
- **Exactly one reminder.** The 7-day reminder fires once per invitee (Open survey, `Completed == false`, `SentAt ≥ 7 days ago`, `ReminderSentAt is null`), stamping `ReminderSentAt` so it never repeats.
- **Public responses are always Anonymous + `InputMethod=Slug`.** The slug path requires `AllowAnonymous`; reserved slugs `admin`/`answer` are rejected by the builder and 404 on the answer path.
- **Single-repo ownership.** Only `SurveyRepository` touches the six `survey_*` tables (`[Section("Survey")]`); a `survey_*` table appears in no other repository.
- **Cross-domain refs are bare `Guid` FK columns** — no navigation properties, no `[Obsolete]` navs, and no cross-section EF FK constraints. Display data (creator/respondent names, recipient languages/emails) is stitched into DTOs by the service via `I…ServiceRead` interfaces.

## Negative Access Rules

- No code outside `SurveyRepository` (and `SurveyService` above it) **cannot** read/write `survey_*` tables; other sections **cannot** inject `ISurveyRepository` (pinned by `SurveyArchitectureTests`).
- Survey code **cannot** reach into other sections' data or repositories — cross-section data comes **only** through `IUserServiceRead`/`ITeamServiceRead`/`ITicketServiceRead`/`IShiftView`/`IUserEmailService`.
- Results, exports, and the API **cannot** expose respondent identity for CompletionTracked or Anonymous responses — `UserId`/`UserName` are populated **only** for Identified rows (enforced server-side regardless of API params).
- The system **cannot** store a completion timestamp for CompletionTracked responses (timing side-channel).
- Individual response submissions **cannot** be audit-logged (would re-link an anonymous answer to a time/actor).
- Public-slug requests **cannot** carry identity or a non-Anonymous tier; `/Survey/Admin` and `/Survey/Answer` **cannot** be claimed as a public slug.

## Triggers

- When a survey is created / updated / opened / closed, an audit entry is written via `IAuditLogService.LogAsync` (`AuditAction.SurveyCreated` / `SurveyUpdated` / `SurveyOpened` / `SurveyClosed`).
- When invitations are sent, net-new `SurveyInvitation` rows are created, each email is queued via `IEmailService.SendAsync` with `IEmailMessageFactory.SurveyInvitation` in the recipient's preferred language (`SentAt`+`LatestEmailStatus=Queued`; `Failed` on a synchronous throw), and one `AuditAction.SurveyInvitesSent` entry is logged.
- When the daily `survey-reminder` recurring job (`SendSurveyReminderJob`, cron `0 9 * * *`) runs, `SurveyService.SendDueRemindersAsync` queues one `IEmailMessageFactory.SurveyReminder` per due invitee, stamps `ReminderSentAt`, and logs `AuditAction.SurveyReminderSent` (job actor). The job touches no repository.
- When a response is submitted, the response + answers are written in one save; `Invitation.Completed` is flipped for Identified/CompletionTracked. **No audit entry** is written for the submission.
- When the invited wizard advances past the intro, `Invitation.Started` is set; on the public path, `Survey.PublicStartedCount` is incremented.
- When the GDPR export runs, `SurveyService` (as `IUserDataContributor`) contributes the user's **Identified** responses under `GdprExportSections.SurveyResponses`.

## Cross-Section Dependencies

- **Users/Identity:** `IUserServiceRead` — resolve active-member ids for audience, and creator/respondent/recipient display names + preferred languages.
- **Teams:** `ITeamServiceRead` — `Team`-audience member ids and team display data.
- **Tickets:** `ITicketServiceRead` — `TicketHolders`-audience recipient ids.
- **Shifts:** `IShiftView` — `ShiftParticipants`-audience recipient ids.
- **Profiles:** `IUserEmailService` — effective notification target email per recipient when queueing invite/reminder mail.
- **Email:** `IEmailService.SendAsync` with `IEmailMessageFactory.SurveyInvitation` / `SurveyReminder` — invite + reminder mail (queued through the email outbox in production).
- **GoogleIntegration:** `IGoogleTranslationService` — the builder's "Save + translate missing" pre-fills blank cultures from the default culture (Cloud Translation; dev stub returns `[xx]`-prefixed text). Fills blanks only — authored text is never overwritten.
- **Audit Log:** `IAuditLogService.LogAsync` — survey lifecycle + send/reminder events (never individual submissions).
- **Data Protection:** `IDataProtectionProvider` via `ISurveyInviteTokenProvider` — time-limited, tamper-evident invite tokens (`/Survey/Answer?t={token}`).
- **GDPR:** implements `IUserDataContributor` to export the user's Identified survey responses under `GdprExportSections.SurveyResponses`.

## Architecture

**Owning services:** `SurveyService`
**Owned tables:** `surveys`, `survey_questions`, `survey_question_options`, `survey_invitations`, `survey_responses`, `survey_answers`
**Status:** (A) Migrated — born §15-compliant (peterdrier/Humans issue nobodies-collective/Humans Survey section, 2026-06-04).

- `SurveyService` lives in `Humans.Application.Services.Surveys` and never imports `Microsoft.EntityFrameworkCore`. Implements `ISurveyService`, `ISurveyServiceRead`, and `IUserDataContributor`.
- `ISurveyRepository` (impl `Humans.Infrastructure/Repositories/Surveys/SurveyRepository.cs`, `internal sealed`) is the only code path that touches the six `survey_*` tables via `DbContext`. Registered as Singleton; uses `IDbContextFactory<HumansDbContext>` for per-call scoped contexts.
- **Aggregate-local navs kept:** `Survey.Questions ↔ SurveyQuestion.Survey`, `SurveyQuestion.Options ↔ SurveyQuestionOption.Question`, `SurveyResponse.Answers ↔ SurveyAnswer.Response`. All within Survey-owned tables, so these `.Include`s are legal inside the repository.
- **Decorator decision — no caching decorator.** Admin-authored, low-traffic, per-invitee writes — not a hot bulk-read path (Feedback/Issues rationale). Registered as a plain Scoped service.
- **Cross-domain navs — none.** Survey references Users/Teams by **bare `Guid` FK columns only** (the clean `FeedbackReport.AgentConversationId` precedent / `memory/architecture/no-cross-section-ef-joins.md`), with **no `[Obsolete]` navs and no cross-section EF FK constraints** — the older `Issue`/`Feedback`/`Camp` nav-stitching debt is not propagated. The service resolves display data via the cross-section read interfaces and returns DTOs.
- **Cross-section calls — the public interfaces this section consumes:** `IUserServiceRead`, `ITeamServiceRead`, `ITicketServiceRead`, `IShiftView`, `IUserEmailService`, `IEmailService`, `IEmailMessageFactory`, `IAuditLogService`, `IDataProtectionProvider` (via `ISurveyInviteTokenProvider`).
- **Architecture test** — `tests/Humans.Application.Tests/Architecture/SurveyArchitectureTests.cs` pins the section shape and the `ISurveyRepository` consumer allow-list; `ServiceBoundaryArchitectureTests.RepositoryOwners` maps `ISurveyRepository → Survey`. Build-time analyzers HUM0017/HUM0025 enforce single-owner table access and no cross-section repository injection.

### Cross-section read interface

| Read interface | Methods | Notes |
|---|---:|---|
| `ISurveyServiceRead` | 0 | Boundary established; empty in v1 — nothing cross-section consumes Survey yet. Methods returning DTOs are added when a consumer appears. |
