<!-- freshness:triggers
  src/Sections/Humans.Surveys/**
-->
<!-- freshness:flag-on-change
  Anonymity encoding (Identified/CompletionTracked/Anonymous), the no-completion-timestamp rule, server-side branching, idempotent invite ledger, and the no-cross-section-nav FK style — review when Survey service/entities/configs/controllers change.
-->

# Survey — Section Invariants

First-party, GDPR-compliant surveys: author typed/branching multi-language surveys, send tokenised email invitations to a resolved audience, collect responses across three anonymity tiers (invite link or public slug), and read results in-app, via CSV/JSON export, and a key-authed analysis API.

## Concepts

- A **Survey** is an authored questionnaire with per-culture title/intro/thank-you and optional invitation email subject/message (`LocalizedText`), a default culture, a status lifecycle (Draft → Open → Closed), an optional open/close window, an audience, and an optional public slug. It owns an ordered graph of questions.
- A **SurveyQuestion** is one ordered item on a page, typed (SingleChoice, MultiChoice, ShortText, LongText, Rating, Grid, Information), optionally required, optionally gated by a **`ShowIf` branch condition**. Choice questions own ordered **SurveyQuestionOptions** (stable machine `Value` + `LocalizedText` label). Grid questions reuse those options as columns and store localized, stable-keyed rows directly on the question. Information items reuse Prompt as an optional heading and HelpText as sanitized Markdown, and may carry up to five labelled public images.
- A **branch condition** (`BranchCondition` + `BranchClause`, jsonb) is skip-logic: a question is visible only when its clauses (combined `All`/`Any`, operators `Is`/`IsNot`/`Answered`/`NotAnswered` over earlier questions' option values) evaluate true. Evaluated by the pure `SurveyBranchingEvaluator`.
- A **SurveyInvitation** is the per-recipient ledger row for the invited path: one per `(SurveyId, UserId)`. It carries send/reminder funnel state (`SentAt`, `LatestEmailStatus`, `ReminderSentAt`, `Started`, `Completed`) — all flags/timestamps about *participation*, never about *answer content*.
- A **SurveyResponse** is one submitted (or, for Identified, in-progress) answer set, tagged with its **anonymity tier** and **input method** (UserSpecificLink vs Slug). It owns **SurveyAnswers** (selected option values, free text, rating, or Grid row-to-column selections).
- **Anonymity tiers** (`ResponseAnonymity`): **Identified** (linked to the invitee, resumable, the only personal-data tier), **CompletionTracked** (participation counted, answers unlinkable), **Anonymous** (no trace). See Invariants.
- The **public-slug path** (`/Survey/{slug}`) has no emailed token. Logged-out visitors are always Anonymous. Logged-in Humans choose Identified, CompletionTracked, or Anonymous; tracked tiers use the existing per-survey/user participation ledger and all responses retain `InputMethod=Slug`.

## Data Model

### Survey

**Table:** `surveys`

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| Title / Intro / ThankYou | LocalizedText | jsonb (culture → text); default `'{}'::jsonb`. Intro is raw Markdown rendered only through the shared sanitized-Markdown helper. |
| InvitationEmailSubject / InvitationEmailMessage | LocalizedText | optional custom initial-invitation copy; jsonb default `'{}'::jsonb` means use standard localized wording |
| DefaultCulture | string | max 10; fallback culture for resolution |
| AllowAnonymous | bool | gates the anonymity selector and the public slug |
| Status | SurveyStatus | string-converted; Draft / Open / Closed |
| OpensAt / ClosesAt | Instant? | optional open/close window |
| AudienceType | SurveyAudienceType? | string-converted; null = no audience |
| AudienceTeamId | Guid? | bare FK → Team (when `AudienceType = Team`) — **FK only**, no nav, no cross-section EF FK constraint |
| AudienceLoggedInSince | Instant? | cutoff (when `AudienceType = LoggedInSince`); users with `LastLoginAt >= cutoff` match, `null` LastLoginAt never matches |
| PublicSlug | string? | max 80; shareable answering link; identified surveys require sign-in and current audience access; null = invite-only |
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
| GridSelectionMode | GridSelectionMode? | string-converted; Single / Multiple for Grid questions |
| GridRows | List&lt;SurveyGridRow&gt;? | jsonb; ordered stable row `Value` + localized label |
| InformationImages | List&lt;SurveyInformationImage&gt;? | nullable jsonb; up to five public storage keys with localized label and alt text |
| ShowIf | BranchCondition? | jsonb skip-logic |

**Index:** `(SurveyId, PageNumber, Order)`.

### SurveyQuestionOption

**Table:** `survey_question_options` — aggregate-local to `survey_questions` (Cascade).

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| QuestionId | Guid | FK → SurveyQuestion, Cascade |
| Order | int | |
| Value | string | max 100; stable machine key (not localised; used as branching/export join key, or as a Grid column key) |
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
| GridSelections | Dictionary&lt;string, List&lt;string&gt;&gt;? | jsonb; stable row key → selected stable column keys |

### Enums

| Enum | Values |
|------|--------|
| SurveyStatus | Draft, Open, Closed |
| SurveyQuestionType | SingleChoice, MultiChoice, ShortText, LongText, Rating, Grid, Information |
| GridSelectionMode | Single, Multiple |
| ResponseAnonymity | Identified, CompletionTracked, Anonymous |
| SurveyInputMethod | UserSpecificLink, Slug |
| SurveyAudienceType | Team, AllActiveMembers, TicketHolders, ShiftParticipants, LoggedInSince |
| BranchCombine | All, Any |
| BranchOperator | Is, IsNot, Answered, NotAnswered |

`LocalizedText` (culture → text), `SurveyGridRow`, `SurveyInformationImage`, and `BranchCondition`/`BranchClause` are section-owned value objects in `src/Sections/Humans.Surveys/Domain/`; localized text, Grid rows/selections, Information images, and branch conditions are persisted as jsonb.

## Routing

- **`/Survey/Admin/*`** — `SurveyAdminController` (BoardOrAdmin): index, builder, read-only preview,
  preview-email-to-self, send, results, CSV/JSON export.
  The builder's **Save and review recipients** action continues to the Send page; a Draft with
  net-new recipients can be opened there before the separate invitation confirmation.
- **`/Survey/Answer?t={token}`** — `SurveyController` invited wizard (token carries identity; never the current principal).
- **`/Survey/{slug}`** — `SurveyController` public wizard: logged-out visitors are Anonymous; logged-in Humans choose how they are represented. Literal segments `Admin`/`Answer` are **reserved slugs** and resolve before `{slug}`.
- **`/api/backdoor/surveys/*`** — `BackdoorSurveysController` in `Humans.Backdoor` (key-authed, read-only; reads this section through `ISurveyAnalysisRead`).

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| BoardOrAdmin (`PolicyNames.BoardOrAdmin`) | Author surveys (builder), open/close, send invitations, view results + Identified drill-down, export CSV/JSON. |
| Invited member | Answer their invited survey via the tokenised link; choose anonymity tier when `AllowAnonymous`; resume an unfinished Identified draft. Reachable even for non-members (`Survey` is in `MembershipRequiredFilter.ExemptControllers`; answer actions are `[AllowAnonymous]`). |
| Public visitor | Logged out: always Anonymous. Logged in: choose Identified, CompletionTracked, or Anonymous. All public-link responses use `InputMethod=Slug`. |
| API (key auth) | List surveys, get a definition, read responses (`?format=md`/json) and aggregates via `/api/backdoor/surveys` — read-only. The controller lives in `Humans.Backdoor` and reads this section through `ISurveyAnalysisRead` (nobodies-collective/Humans#1128); the key is the caller's personal one, 401 when missing, unknown or revoked. |

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
- **A double-submit or refresh on an already-completed tracked invitation lands on the thank-you page, not a 500.** `AdvanceWizardAsync` treats `PrepareSubmissionAsync` reporting the invitation already `Completed` as a normal `Submitted` outcome (the controller clears the session and redirects); only the standalone `SubmitResponseAsync` entry point still throws on the same condition.
- **The thank-you page reads a session completion marker, not the link.** Submit flips `Invitation.Completed`, after which the invite token no longer resolves — so the redirect to thank-you leaves `SurveyCompletion(SurveyId, Culture)` under `survey-thankyou:{token}` / `survey-thankyou:slug:{slug}`, and the page renders the authored copy in the language the respondent *answered* in (not their site UI culture). A thank-you link opened without a marker falls back to resolving the token/slug.
- **A survey with nothing left to show says so; it never reports itself as completed.** When the wizard reaches a page whose questions are all hidden and there is no next visible page, the respondent gets the `Closed` view with `Reason = "empty"` — not the thank-you page. Nothing has been submitted at that point, so no completion marker is written and the session is left alone. Reaching this state usually means the survey has no questions, or its branching hides all of them, and it is meant to be visible as such rather than disguised as a finished response.
- **Branching is server-side and authoritative.** A null `ShowIf` is visible; hidden questions are never treated as required; at submit the full branching is re-evaluated and answers to hidden questions are **dropped/rejected** (the client cannot smuggle them). Author-save rejects `ShowIf` forward-references (`SurveyBranchingEvaluator.ValidateNoForwardReferences`).
- **Grid questions are bounded matrices.** A Grid has at least one localized row, one to five localized columns, and a `Single` or `Multiple` selection mode. Row and column keys are non-blank and unique. A required Grid is complete only when every row has a valid selection; `Single` permits exactly one column per row. Posted selections are normalized against the authored schema before autosave/submission.
- **Grid questions may be branch targets, never branch sources.** A Grid can carry its own `ShowIf`, but author-save rejects any branch clause that references a Grid question.
- **Grid result percentages are row-local.** Each cell's percentage uses respondents who answered that row as its denominator. Results retain the current authored matrix. The admin results page can aggregate Combined, Identified/CompletionTracked ("unique"), or Anonymous responses; participation cards and funnel remain combined. The JSON download stores question/Grid metadata once in its top-level schema and keeps each response answer to stable keys/values. CSV/Markdown export, the analysis API, and GDPR export retain raw stored row/column keys just as choice exports retain `SelectedOptionValues`, alongside best-effort labels in the survey's default culture; removed definitions fall back to their raw keys instead of hiding historical answers.
- **Response rate belongs to the invited pool.** It is completed sent invitations divided by sent invitations, using the participation ledger rather than submitted-response rows. Identified and CompletionTracked invited completions count; Anonymous responses and public participation rows with no `SentAt` do not.
- **Information items are context, not answers.** They share question ordering, paging, translation, preview, and conditional visibility, but are never required, never branch sources, emit no answer input, and are omitted from results and response exports. Markdown uses the shared sanitizer. Images are public `IFileStorage` objects under `uploads/surveys/`, capped at five, and their builder upload form warns that URLs can be shared outside the survey.
- **Question help text is sanitized Markdown.** Existing plain text remains valid; respondent and preview rendering use the shared Markdown renderer/sanitizer. Prompts remain plain text.
- **Invitation send is idempotent and additive.** Each send resolves the audience, diffs against existing `(SurveyId, UserId)` invitations, and creates+emails only net-new recipients; nobody is double-invited and **sends never revoke**. The Send page previews that exact net-new count, not the raw audience size. Requires the survey Open with a valid audience configuration (`Team` requires a team; `LoggedInSince` requires a cutoff date). Invites are operational (`MessageCategory.System`, always-send) — surveys are never marketing.
- **Survey preview is side-effect-free and status-independent.** Board/Admin authors may preview Draft,
  Open, or Closed surveys through the respondent views. Preview page navigation is GET-only, displays
  all authored conditional questions for inspection, and never creates an invitation, response, draft,
  reminder, completion, or funnel event. The final Submit control is disabled.
- **Preview email reuses the real invitation pipeline without joining its ledger.** It targets the
  current Board/Admin user's canonical notification email and uses
  `IEmailMessageFactory.SurveyInvitation` + `IEmailService.SendAsync`. Its seven-day signed token has a
  distinct Data Protection purpose, retains the recipient's resolved culture, and redirects
  `/Survey/Answer` to the protected preview route; no `SurveyInvitation` row is created.
- **Invitation copy is optional, localized, and plain text.** Authors may replace the initial
  invitation subject and message, while Humans retains the greeting, survey-title heading, generated
  answer-link button, sign-off, template key, and System routing policy. Blank custom fields preserve
  the standard localized wording. Messages are HTML-encoded with line breaks preserved; Markdown,
  raw HTML, author-provided links, and reminder customization are not supported.
- **Exactly one reminder, and only while the survey can still be answered.** The 7-day reminder fires once per invitee (`Completed == false`, `SentAt ≥ 7 days ago`, `ReminderSentAt is null`), stamping `ReminderSentAt` so it never repeats. The sweep re-checks `SurveyWizardFlow.IsAnswerable` per survey — Open alone is not enough, since a reminder outside the `OpensAt`/`ClosesAt` window would link to the Closed page and spend the invitee's one stamp doing it.
- **Shareable slugs support both anonymous and identified surveys.** When `AllowAnonymous` is true, logged-out visitors are Anonymous and logged-in Humans may choose Identified, CompletionTracked, or Anonymous. When it is false, the visitor must sign in and must currently belong to the configured audience (or may be any logged-in Human when no audience is configured). Ordinary surveys then force Identified; Asociado votes force CompletionTracked after verifying current Asociado eligibility. Slug access is rechecked on entry and every wizard GET/POST. All representations use `InputMethod=Slug`. Reserved slugs `admin`/`answer` are rejected by the builder and 404 on the answer path.
- **Asociado ballots are eligibility-tracked but unlinkable.** The participation ledger records which eligible Asociado completed the vote and blocks a second submission, but the final CompletionTracked response stores neither `UserId` nor `InvitationId`, and the ledger stores no completion timestamp. Open results remain embargoed. After close, individual ballots may be inspected without names or timestamps; exports and the Backdoor API suppress identity for all Asociado-vote rows.
- **Tracked public wizard state is principal-bound.** Identified/CompletionTracked slug sessions continue only while the current authenticated Human matches the state’s `UserId`; logout or account switching clears that slug state before any answers render or submit. Fully Anonymous state remains principal-independent.
- **An Identified draft follows its final entry path and privacy choice.** Autosave and final submission stamp the active route and culture, so a draft resumed through a token or slug lands in the correct funnel. Choosing CompletionTracked retires the personal draft and answers when the unlinkable response is submitted.
- **CompletionTracked public participation has no correlatable start time.** A newly created unsent ledger row uses a shared epoch `CreatedAt` sentinel rather than the Human's actual public-start time.
- **Public participation rows are not emailed invitations until email is actually prepared.** A logged-in tracked public start may create a `survey_invitations` ledger row with `SentAt = null`. It is excluded from invited counts/status/reminders. If still incomplete, a later send upgrades that same row by stamping `SentAt` and queue status; if already completed through the public link, the Human is excluded rather than emailed a spent token.
- **A choice option's `Value` must be non-empty.** `AnswerState.IsAnswered` (`SurveyWizardFlow.cs`) counts an answer only when the option value is non-empty, and `SurveyQuestionOption.Value` defaults to `string.Empty` — an option saved with a blank `Value` makes a required question unsubmittable and cannot be named by a `ShowIf` clause.
- **Options carry no free-text flag.** `SurveyQuestionOption` is `Order` + `Value` + `Label` only, so "Other — please specify" is authored as a separate optional `ShortText` question gated by `ShowIf` on the `other` option value.
- **The anonymity chooser pre-selects Identified.** The answer view model defaults `Anonymity` to `ResponseAnonymity.Identified` and `Survey/Intro.cshtml` marks that radio `checked`; the unlinked tiers are opt-in per respondent.
- **Single-repo ownership.** Only `SurveyRepository` touches the six `survey_*` tables; a `survey_*` table appears in no other repository.
- **Cross-domain refs are bare `Guid` FK columns** — no navigation properties, no `[Obsolete]` navs, and no cross-section EF FK constraints. Display data (creator/respondent names, recipient languages/emails) is stitched into DTOs by the service via `I…ServiceRead` interfaces.

## Negative Access Rules

- Code outside `SurveyRepository` (and `SurveyService` above it) **cannot** read/write `survey_*` tables; other sections **cannot** inject `ISurveyRepository` — it is `internal` (HUM0034), so cross-section injection does not compile.
- Survey code **cannot** reach into other sections' data or repositories — cross-section data comes **only** through `IUserServiceRead`/`ITeamServiceRead`/`ITicketServiceRead`/`IShiftView`/`IUserEmailService`.
- Results, exports, and the API **cannot** expose respondent identity for CompletionTracked or Anonymous responses, or for any response belonging to an Asociado vote — `UserId`/`UserName` are populated only for Identified rows in ordinary surveys (enforced server-side regardless of API params).
- The system **cannot** store a completion timestamp for CompletionTracked responses (timing side-channel).
- Individual response submissions **cannot** be audit-logged (would re-link an anonymous answer to a time/actor).
- Logged-out public-slug requests **cannot** carry identity or a non-Anonymous tier. Logged-in public requests cannot attach identity without the respondent's explicit tier choice; CompletionTracked/Anonymous response rows cannot carry identity. `/Survey/Admin` and `/Survey/Answer` **cannot** be claimed as a public slug.
- The `LoggedInSince` audience **cannot** include GDPR-anonymized, deletion-pending, or merged users, or users in `Rejected`/`Suspended`/`AdminSuspended` state — status-walled accounts that can't reach the survey are never invited, even if they logged in after the cutoff (nobodies-collective/Humans#1099).

## Triggers

- When a survey is created / updated / opened / closed, an audit entry is written via `IAuditLogService.LogAsync` (`AuditAction.SurveyCreated` / `SurveyUpdated` / `SurveyOpened` / `SurveyClosed`). `SurveyUpdated` descriptions name the changed fields (audience and slug transitions spelled out; question edits collapsed to counts).
- When invitations are sent, net-new `SurveyInvitation` rows are created, each email is queued via `IEmailService.SendAsync` with `IEmailMessageFactory.SurveyInvitation` in the recipient's preferred language (`SentAt`+`LatestEmailStatus=Queued`; `Failed` on a synchronous throw), and one `AuditAction.SurveyInvitesSent` entry is logged.
- When the daily `surveys-reminder` recurring job (`SendSurveyReminderJob`, cron `0 9 * * *`) runs, `SurveyService.SendDueRemindersAsync` queues one `IEmailMessageFactory.SurveyReminder` per due invitee, stamps `ReminderSentAt`, and logs `AuditAction.SurveyReminderSent` (job actor). The job touches no repository.
- When a response is submitted, the response + answers and `Invitation.Completed` are written in one save for Identified/CompletionTracked. **No audit entry** is written for the submission.
- When the invited wizard advances past the intro, `Invitation.Started` is set; on the public path, `Survey.PublicStartedCount` is incremented.
- When the GDPR export runs, `SurveyService` (as `IUserDataContributor`) contributes the user's **Identified** responses under `GdprExportSections.SurveyResponses`.
- When Article 17 erasure runs, `EraseForUserAsync` deletes the user's `SurveyInvitation` rows and severs their Identified responses from the person (`UserId`/`InvitationId` dropped, `Anonymity` forced to `Anonymous`) — the answers themselves survive as an anonymous data point in the survey's results (Art. 17(3)(b)), which is what `ErasureDeclaration` names as partial retention for `GdprExportSections.SurveyResponses`.

## Cross-Section Dependencies

- **Users/Identity:** `IUserServiceRead` — resolve active-member ids and `LoggedInSince`-audience ids (`UserInfo.LastLoginAt`), and creator/respondent/recipient display names + preferred languages.
- **Teams:** `ITeamServiceRead` — `Team`-audience member ids and team display data.
- **Tickets:** `ITicketServiceRead` — `TicketHolders`-audience recipient ids.
- **Shifts:** `IShiftView` — `ShiftParticipants`-audience recipient ids.
- **Users/Identity:** `IUserEmailService` — effective notification target email per recipient when queueing invite/reminder mail.
- **Email:** `IEmailService.SendAsync` with `IEmailMessageFactory.SurveyInvitation` / `SurveyReminder` — invite + reminder mail (queued through the email outbox in production); `IEmailPreviewServiceRead` renders the preview-email-to-self page.
- **Files:** `IFileStorage` — public objects under `uploads/surveys/` for Information-item images.
- **GoogleIntegration:** `IGoogleTranslationService` — the builder's "Save + translate missing" pre-fills blank cultures from the default culture (Cloud Translation; dev stub returns `[xx]`-prefixed text). Fills blanks only — authored text is never overwritten.
- **Audit Log:** `IAuditLogService.LogAsync` — survey lifecycle + send/reminder events (never individual submissions).
- **Data Protection:** `IDataProtectionProvider` via `ISurveyInviteTokenProvider` and
  `SurveyPreviewTokenProvider` — time-limited, tamper-evident invitation and preview tokens with
  distinct purposes (`/Survey/Answer?t={token}`).
- **GDPR:** implements `IUserDataContributor` to export the user's Identified survey responses under `GdprExportSections.SurveyResponses`, and to erase them on Article 17 request (invitation deleted, response demoted to Anonymous — see Triggers).

## Architecture

**Owning services:** `SurveyService`
**Owned tables:** `surveys`, `survey_questions`, `survey_question_options`, `survey_invitations`, `survey_responses`, `survey_answers`
**Status:** (A) Migrated — born §15-compliant. Everything but `Section`, `SurveysResource`, `Contracts/` and `Jobs/` is `internal` (HUM0034). `Contracts/` is entirely public: the enums, the `SurveyDefinitionSnapshot`/`SurveyReadModels` DTOs, `SurveyResponsesMarkdownBuilder`, `ISurveyReminderSender.SendDueRemindersAsync`, and `ISurveyAnalysisRead` (the Backdoor API's read surface — see Cross-section read interface below). `Jobs/SendSurveyReminderJob.cs` is public under the HUM0034 `Jobs/` carve-out because the Shell names the concrete type when it registers and schedules it.

- `SurveyService` lives in `Humans.Surveys.Services` and never imports `Microsoft.EntityFrameworkCore`. Implements `ISurveyService` and `IUserDataContributor`.
- `ISurveyRepository` (impl `src/Sections/Humans.Surveys/Data/SurveyRepository.cs`, `internal sealed`) is the only code path that touches the six `survey_*` tables via `DbContext`. Registered as Singleton; uses `IDbContextFactory<SurveysDbContext>` (per-section DbContext, nobodies-collective/Humans#858; baseline migration `20260715105933_BaselineSurveys` under `Data/Migrations/`) for per-call scoped contexts.
- **Aggregate-local navs kept:** `Survey.Questions ↔ SurveyQuestion.Survey`, `SurveyQuestion.Options ↔ SurveyQuestionOption.Question`, `SurveyResponse.Answers ↔ SurveyAnswer.Response`. All within Survey-owned tables, so these `.Include`s are legal inside the repository.
- **Decorator decision — no caching decorator.** Admin-authored, low-traffic, per-invitee writes — not a hot bulk-read path (Feedback/Issues rationale). Registered as a plain Scoped service.
- **Cross-domain navs — none.** Survey references Users/Teams by **bare `Guid` FK columns only** (`memory/architecture/no-cross-section-ef-joins.md`), with **no `[Obsolete]` navs and no cross-section EF FK constraints**. The service resolves display data via the cross-section read interfaces and returns DTOs.
- **Cross-section calls — the public interfaces this section consumes:** `IUserServiceRead`, `ITeamServiceRead`, `ITicketServiceRead`, `IShiftView`, `IUserEmailService`, `IEmailService`, `IEmailMessageFactory`, `IEmailPreviewServiceRead`, `IAuditLogService`, `IGoogleTranslationService`, `IFileStorage`, `IDataProtectionProvider` (via `ISurveyInviteTokenProvider` and `SurveyPreviewTokenProvider`).
- **Architecture test** — `tests/Humans.Surveys.Tests/SurveysArchitectureTests.cs` pins the section shape. HUM0025 enforces single-owner table access; cross-section repository injection does not compile, `ISurveyRepository` being internal — a per-consumer allow-list test would only assert absence ([`no-tests-for-absences`](../../../../memory/architecture/no-tests-for-absences.md)).

### Cross-section read interface

`ISurveyAnalysisRead` (public, in `Contracts/`): survey list, one survey's resolved question graph (`GetDefinitionAsync` → `SurveyDefinitionSnapshot`), the raw per-response export, and the per-question aggregates. Read-only — a survey is authored only in the admin UI, never over the API. Its sole consumer is `Humans.Backdoor`'s `BackdoorSurveysController` behind `/api/backdoor/surveys`. The section's only other public behaviour surface is `ISurveyReminderSender`, consumed by `Jobs/SendSurveyReminderJob`. There is deliberately **no `ISurveyServiceRead`**: it shipped empty, no other section consumed it, and it was deleted — the machine API's needs are a different shape and are served by `ISurveyAnalysisRead`.
