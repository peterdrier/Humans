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
| IsAsociadoVote | bool? | binding Asociado vote (see Invariants); null and false both mean an ordinary survey |
| Status | SurveyStatus | string-converted; Draft / PendingApproval / Open / Closed |
| OpensAt / ClosesAt | Instant? | optional open/close window |
| AudienceType | SurveyAudienceType? | string-converted; null = no audience |
| AudienceTeamId | Guid? | bare FK → Team (when `AudienceType = Team`) — **FK only**, no nav, no cross-section EF FK constraint |
| AudienceLoggedInSince | Instant? | cutoff (when `AudienceType = LoggedInSince`); users with `LastLoginAt >= cutoff` match, `null` LastLoginAt never matches |
| PublicSlug | string? | max 80; shareable answering link; identified surveys require sign-in and current audience access; null = invite-only |
| PublicStartedCount | int | slug-path "started" funnel counter (no per-person anchor) |
| CreatedByUserId | Guid | bare FK → User — **FK only**, no nav, resolved via `IUserService` |
| SubmittedAt | Instant? | stamped entering PendingApproval; cleared on approve/reject |
| RejectionNote | string? | max 4000; validated before rejection persists; invalid notes remain in the queue form for correction; Board/Admin's note when a submission is rejected back to Draft; cleared on resubmit/approve |
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
| RankedSettings | RankedQuestionSettings? | jsonb; allow equal ranks, allow reject, official method; RankedChoice only, frozen at the first saved answer |
| RankedUnavailableOptionValues | List&lt;string&gt;? | jsonb; option values struck from the count after close; never rewrites a ballot |
| ShowIf | BranchCondition? | jsonb skip-logic |

**Index:** `(SurveyId, PageNumber, Order)`.

### SurveyQuestionOption

**Table:** `survey_question_options` — aggregate-local to `survey_questions` (Cascade).

| Property | Type | Notes |
|----------|------|-------|
| Id | Guid | PK |
| QuestionId | Guid | FK → SurveyQuestion, Cascade |
| Order | int | |
| Value | string | max 100; create/update refuse overlong keys before persistence without truncating; stable machine key (not localised; used as branching/export join key, or as a Grid column key) |
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
| RankedValue | RankedAnswer? | jsonb; ordered rank groups of stable option values plus a rejected set |

### Enums

| Enum | Values |
|------|--------|
| SurveyStatus | Draft, PendingApproval, Open, Closed |
| SurveyQuestionType | SingleChoice, MultiChoice, ShortText, LongText, Rating, Grid, Information, RankedChoice |
| GridSelectionMode | Single, Multiple |
| ResponseAnonymity | Identified, CompletionTracked, Anonymous |
| SurveyInputMethod | UserSpecificLink, Slug |
| SurveyAudienceType | Team, AllActiveMembers, TicketHolders, ShiftParticipants, LoggedInSince, Asociados |
| BranchCombine | All, Any |
| BranchOperator | Is, IsNot, Answered, NotAnswered |

`LocalizedText` (culture → text), `SurveyGridRow`, `SurveyInformationImage`, `RankedQuestionSettings`, `RankedAnswer`, and `BranchCondition`/`BranchClause` are section-owned value objects in `src/Sections/Humans.Surveys/Domain/`; all are persisted as jsonb.

## Routing

- **`/Survey/Admin/*`** — `SurveyAdminController` (any Human with an approved profile, `PolicyNames.AppAccess`):
  index (author-scoped: an author sees only their own surveys; Board/Admin see all), builder (create/edit
  a Draft they own; submit for approval), preview, preview-email-to-self, send, results, CSV/JSON export.
  Board/Admin only: open/close, the official link, send invitations, post-close ranked-option
  availability, send/results/export and preview on surveys they don't own, and the approval queue at
  `/Survey/Admin/Queue` (approve-and-send or reject-with-note a `PendingApproval` submission). Per-survey ownership and the approval gate are enforced by `SurveyAuthorizationHandler`
  (design-rules §11), never by view filtering; `SurveyService` re-checks ownership on every edit path
  (`UpdateAsync`, translation pre-fill, ranked availability), so an edit the handler refuses is refused
  whoever the caller is.
  Preview wizard banners, their email-send control and the email-preview page use the site UI language in all six cultures. Builder and results status badges reuse the localized survey-status captions. The authoring question-type selector localizes all eight captions in all six cultures while posting the unchanged enum values. Their breadcrumb navigation labels use the site UI language in all six cultures.
  Ranked-result sensitivity tables localize method captions in all six cultures; the official-method highlight uses the result identity rather than English caption text.
  Ordinary authors’ create/save and preview-email notices use the site UI language in all six cultures. Create/update validation returns internal refusal keys and arguments before persistence; the builder retains entered fields for correction. Failed image validation or later definition validation cleans newly uploaded files with an uncancelled token. After a persistence error, uploads are removed only when an uncancelled read confirms no survey exists; an existing survey or failed verification preserves them and the original error. Dependency diagnostics stay in logs; a failed post-save translation, including I/O, HTTP or provider cancellation failures, reports the committed save and redirects without exposing exception text. I/O, HTTP and provider cancellation failures log Error with the original exception. Request cancellation still propagates and stops paid translation work. Invalid public URL slugs (blank, reserved or over 80 Unicode characters) resolve as not found; authoring keeps its strict rejection before uploads and writes. Preview errors (missing survey, missing notification address, failed queueing) return ordinary refusal keys localized by the controller, independently of the invitation’s recipient language. Save and preview refusal results log Warning with applicable survey/user identifiers and retain localized feedback. Unexpected dependency failures propagate to normal error handling rather than being presented as validation refusals.
  The builder's **Save and review recipients** action continues to the Send page; a Draft with
  net-new recipients can be opened there before the separate invitation confirmation.
- **`/Survey/Answer?t={token}`** — `SurveyController` invited wizard (token carries identity; never the current principal).
- **`/Survey/{slug}`** — `SurveyController` public wizard: logged-out visitors are Anonymous; logged-in Humans choose how they are represented. Literal segments `Admin`/`Answer` are **reserved slugs** and resolve before `{slug}`.
- **`/api/backdoor/surveys/*`** — `BackdoorSurveysController` in `Humans.Backdoor` (key-authed, read-only; reads this section through `ISurveyAnalysisRead`).

## Actors & Roles

| Actor | Capabilities |
|-------|--------------|
| Author (any Human with an approved profile, `PolicyNames.AppAccess`) | Create/edit a Draft survey they own (may be Identified — not forced anonymous), preview it — the respondent renderings and an invitation email to themselves — submit it for Board/Admin approval, view their own results/export only after that survey closes. Cannot see or act on anyone else's survey; cannot see their own results before closing. |
| BoardOrAdmin (`PolicyNames.BoardOrAdmin`) | Everything an author can do on every survey, plus: open/close directly, send invitations, the approval queue (approve-and-send or reject-with-note any `PendingApproval` submission), view results + Identified drill-down, export CSV/JSON, for every survey regardless of author. |
| Invited member | Answer their invited survey via the tokenised link; choose anonymity tier when `AllowAnonymous`; resume an unfinished Identified draft. Reachable even for non-members (the controller is `[AllowAnonymous]`, which the membership gate passes through). |
| Public visitor | Logged out: always Anonymous. Logged in: choose Identified, CompletionTracked, or Anonymous. All public-link responses use `InputMethod=Slug`. |
| API (key auth) | List surveys, get a definition, read responses (`?format=md`/json) and aggregates via `/api/backdoor/surveys` — read-only. The controller lives in `Humans.Backdoor` and reads this section through `ISurveyAnalysisRead` (nobodies-collective/Humans#1128); the key is the caller's personal one, 401 when missing, unknown or revoked. |

## Invariants

- Invited wizard starts reject invalid model binding and undefined anonymity values with HTTP 400 before creating drafts, marking an invitation started or storing wizard state. A malformed privacy choice never silently uses the model’s Identified default.

- UI resource keys use the section prefix `Surveys_` in all six cultures; reflection-resolved `Enum_` keys retain their type-based names.

- Survey create and update share the editable-field mapping, including normalized invitation copy, prepared questions and public slug. Creation identity/status/timestamps and each path’s validation and upload lifecycle remain owned by that path.

- Invitation send/reminder timestamps on the operator send page use the shared display formatter in Europe/Madrid time, with date ordering and month names following the UI culture; missing stamps remain an em dash.

- **Anonymity encoding** is the load-bearing privacy contract:

  | Tier | `Response.UserId` | `Response.InvitationId` | `Invitation.Completed` | Resume? | In GDPR export / drill-down / API identity |
  |------|------|------|------|------|------|
  | **Identified** | invitee id | invitation id | true | yes (draft, `SubmittedAt` null) | yes — only personal-data tier |
  | **CompletionTracked** | null | null | true (**bool only — no timestamp**) | no | no |
  | **Anonymous** | null | null | false (untouched) | no | no |

  `UserId`/`InvitationId` are written on the response **only** for Identified. CompletionTracked flips the invitation's `Completed` flag (known from the wizard token) without persisting any link on the response.
- **`Completed` is a boolean with no timestamp** and `survey_invitations` has no `UpdatedAt`: recording *when* a CompletionTracked invitee finished would correlate (user-linked) with the unattributed response's `SubmittedAt` and re-identify them.
- **Resume is Identified-only.** An in-progress Identified response is a persisted draft (`SubmittedAt is null`), found by `(SurveyId, UserId, SubmittedAt is null)`. CompletionTracked/Anonymous carry no link, are held in session, and **restart** on reopen.
- **Response finalization is serialized in-process.** A concurrent double-submit cannot insert two unlinked CompletionTracked ballots: the second finalizer re-reads the completed invitation after the first save and does nothing. Identified draft autosaves share the gate so a stale autosave cannot change a completed response or append extra answers. The gate covers the short persistence step only; anonymity fields and the timestamp-free completion marker stay unchanged.
- **A double-submit or refresh on an already-completed tracked invitation lands on the thank-you page, not a 500.** `AdvanceWizardAsync` treats `PrepareSubmissionAsync` reporting the invitation already `Completed` as a normal `Submitted` outcome (the controller clears the session and redirects). Final submission runs only through the wizard; there is no separate submission entry point.
- **The thank-you page reads a session completion marker, not the link.** Submit flips `Invitation.Completed`, after which the invite token no longer resolves — so the redirect to thank-you leaves `SurveyCompletion(SurveyId, Culture)` under `survey-thankyou:{token}` / `survey-thankyou:slug:{slug}`, and the page renders the authored copy in the language the respondent *answered* in (not their site UI culture). A thank-you link opened without a marker falls back to resolving the token/slug.
- **A survey with nothing left to show says so; it never reports itself as completed.** When the wizard reaches a page whose questions are all hidden and there is no next visible page, the respondent gets the `Closed` view with `Reason = "empty"` — not the thank-you page. Nothing has been submitted at that point, so no completion marker is written and the session is left alone. Reaching this state usually means the survey has no questions, or its branching hides all of them, and it is meant to be visible as such rather than disguised as a finished response.
- **Branching is server-side and authoritative.** A null `ShowIf` is visible; hidden questions are never treated as required; at submit the full branching is re-evaluated and answers to hidden questions are **dropped/rejected** (the client cannot smuggle them). Author-save rejects `ShowIf` forward-references (`SurveyBranchingEvaluator.ValidateNoForwardReferences`).
- **Grid questions are bounded matrices.** A Grid has at least one localized row, one to five localized columns, and a `Single` or `Multiple` selection mode. Row and column keys are non-blank and unique. A required Grid is complete only when every row has a valid selection; `Single` permits exactly one column per row. Posted selections are normalized against the authored schema before autosave/submission.
- **Grid, Information and RankedChoice questions may be branch targets, never branch sources.** Each can carry its own `ShowIf`, but author-save rejects any branch clause that references one — none yields the flat option set a clause matches on.
- **Post-close ranked availability returns save results.** Missing surveys, votes that are not Closed and missing/non-ranked questions return expected refusals before mutation. The Board-only results action logs Warning and displays the refusal; unexpected dependency failures propagate. Successful changes retain normalization to known option values, recounts and the survey update audit.
- **Ranked ballots are validated before persistence.** Both wizard and direct service submission reject repeated options, disallowed equal ranks, and disallowed rejection against the authored settings. Pairwise and Borda counting share one preference-tier builder: active ranked groups first, then unranked candidates, then rejected candidates; equal ranks remain tied.
- **Answers follow the current question schema.** Wizard posts with repeated entries for a visible question return localized invalid-answer feedback before autosave or navigation; neither entry nor a previous answer survives for that question. Unknown or off-page entries remain ignored. Wizard capture and final submission discard fields belonging to other question types, remove unknown/duplicate choice values, reject multiple selections for SingleChoice, and reject ratings outside the authored range (default 1–5). ShortText and LongText answers allow at most 4,000 Unicode characters, matching the database limit. Overlong text returns the existing localized invalid-answer feedback before autosave or submission and stays in the wizard for correction; it is never truncated. Final branching uses normalized answers; removed options cannot satisfy requiredness or branching after a definition change. The authoritative submission reload returns the existing NotFound, Closed or Ineligible outcome when the survey disappears, its response window expires, or voting identity/eligibility is no longer valid. These refusals finalize no response or invitation, log Warning with survey/outcome and no exception stack, and reach the existing respondent feedback instead of throwing. The internal `SurveyAnswerNormalization` helper owns the shared, side-effect-free normalization rules. It returns explicit refusal reasons for single-choice, rating, text-length and ranked-ballot guards; wizard capture and final submission keep the same invalid-answer IDs and correction state without exception-based validation. Existing stored historical answers remain unchanged.
- **Wizard page posts reject malformed values, not missing optional ones.** A page post whose values fail to bind (non-numeric page or rating, unparseable question id) is a 400 before the wizard advances. `Token` (absent on the public path) and blank grid-row / ranked-option keys are `[ValidateNever]`, so their implicit non-nullable `[Required]` never rejects an honest post.
- Rating authoring rejects a minimum above the maximum before create/update persistence, applying the respondent defaults of 1 and 5 when bounds are omitted. Equal bounds remain valid.
- Rating controls use the question's localized prompt as their accessible group label on answering and preview pages. Result averages use the viewer's UI decimal separator and retain one fractional digit; unanswered ratings have no average. Rating controls and result distributions enumerate inclusive integer bounds without wrapping when the upper bound is `int.MaxValue`.
- **Grid result percentages are row-local.** Each cell's percentage uses respondents who answered that row as its denominator. Results retain the current authored matrix. The admin results page can aggregate Combined, Identified/CompletionTracked ("unique"), or Anonymous responses; participation cards and funnel remain combined. The JSON download stores question/Grid metadata once in its top-level schema and keeps each response answer to stable keys/values. CSV/Markdown export, the analysis API, and GDPR export retain raw stored row/column keys just as choice exports retain `SelectedOptionValues`, alongside best-effort labels in the survey's default culture; removed definitions fall back to their raw keys instead of hiding historical answers.
- **Response rate belongs to the invited pool.** It is completed sent invitations divided by sent invitations, using the participation ledger rather than submitted-response rows. Identified and CompletionTracked invited completions count; Anonymous responses and public participation rows with no `SentAt` do not.
- **Information items are context, not answers.** They share question ordering, paging, translation, preview, and conditional visibility, but are never required, never branch sources, emit no answer input, and are omitted from results and response exports. Markdown uses the shared sanitizer. Images are public `IFileStorage` objects under `uploads/surveys/`, capped at five; the builder route permits 55 MB for five 10 MB images plus multipart overhead; their displayed upload filenames are normalized to a basename of at most 256 characters; and their builder upload form warns that URLs can be shared outside the survey. Reserved public slugs and slugs longer than 80 Unicode characters (after trimming) are rejected before any image upload or database write on create or update.
- **Question help text is sanitized Markdown.** Existing plain text remains valid; respondent and preview rendering use the shared Markdown renderer/sanitizer. Prompts remain plain text.
- **The invitation email's custom message has a builder-time "Preview" button** (distinct from the send-a-preview-to-self flow below) that renders the typed Markdown through the shared `_EmailComposer` component, which posts to Email's `POST /Email/PreviewMarkdown` endpoint (`IEmailPreviewManagement.RenderMarkdown`, section-internal) — no invitation is sent and no outbox row is created.
- **Invitation send is additive with at-least-once delivery.** Each send resolves the audience and skips invitations whose `SentAt` records outbox acceptance. Failed unsent rows are reused on retry, including public participation rows; **sends never revoke**. `SentAt` is stamped only after enqueue acceptance. An uncertain enqueue or a failed post-enqueue status save can cause a duplicate email on retry. The Send page previews that exact net-new count, not the raw audience size. Missing surveys, non-Open status and invalid audience configuration return expected send refusals before writes; the Board-only Send page displays those reasons with Warning logs. Unexpected dependency exceptions propagate rather than becoming refusal text. Requires the survey Open with a valid audience configuration (`Team` requires a team; `LoggedInSince` requires a cutoff date). Invites are operational (`MessageCategory.System`, always-send) — surveys are never marketing.
- Intro, page, thank-you, email-render and preview-email-send actions share one survey lookup and Preview authorization gate; email actions still require a signed-in actor before that lookup.
- **Survey preview is side-effect-free and status-independent.** An author may rehearse their own
  survey — Draft, Open, or Closed — through the respondent views, and Board/Admin may rehearse any;
  `SurveyOperation.Preview` refuses a survey you neither wrote nor administer. Preview page
  navigation is GET-only, displays
  all authored conditional questions for inspection, and never creates an invitation, response, draft,
  reminder, completion, or funnel event. The final Submit control is disabled.
- **Preview email reuses the real invitation pipeline without joining its ledger.** It targets the
  rehearsing user's own canonical notification email and uses
  `SurveysEmails.SurveyInvitation` + `IEmailService.SendAsync`. Its seven-day signed token has a
  distinct Data Protection purpose, retains the recipient's resolved culture, and redirects
  `/Survey/Answer` to the protected preview route; no `SurveyInvitation` row is created.
  An aborted preview-email request preserves cancellation rather than logging or wrapping it as
  a queue failure; dependency cancellation without an aborted caller remains a queue failure.
- **Invitation copy is optional, localized, and sanitized Markdown.** Authors may replace the initial
  invitation subject and message, while Humans retains the greeting, survey-title heading, generated
  answer-link button, sign-off, template key, and System routing policy. Blank custom fields preserve
  the standard localized wording. Messages are rendered through the shared sanitized-Markdown
  renderer (Base) — `https://` images and an author's Markdown link both survive as `<img src>`/
  `<a href>`, while anything the sanitizer rejects (scripts, unsafe attributes, non-`https://` image
  sources) does not. Reminder customization is not supported.
- **Reminder titles use the recipient’s language.** Invitations and reminders resolve authored titles in the supported preferred language, falling back to the survey’s default language; a missing translation falls back through `LocalizedText.Resolve`. Survey metadata is loaded once per sweep, but titles are resolved for each recipient.
- **Exactly one reminder, and only while the survey can still be answered.** The 7-day reminder fires once per invitee (`Completed == false`, `SentAt ≥ 7 days ago`, `ReminderSentAt is null`), stamping `ReminderSentAt` so it never repeats. The sweep re-checks `SurveyWizardFlow.IsAnswerable` per survey — Open alone is not enough, since a reminder outside the `OpensAt`/`ClosesAt` window would link to the Closed page and spend the invitee's one stamp doing it.
- **Shareable slugs support both anonymous and identified surveys.** When `AllowAnonymous` is true, logged-out visitors are Anonymous and logged-in Humans may choose Identified, CompletionTracked, or Anonymous. A logged-in visitor’s missing or invalid tier choice is refused before tracking, draft creation, funnel counts or session writes; omission cannot silently attach identity. When it is false, the visitor must sign in and must currently belong to the configured audience (or may be any logged-in Human when no audience is configured). Ordinary surveys then force Identified; Asociado votes force CompletionTracked after verifying current Asociado eligibility. Slug access is rechecked on entry and every wizard GET/POST. All representations use `InputMethod=Slug`. Reserved slugs `admin`/`answer` are rejected by the builder and 404 on the answer path.
- **Eligibility is judged on the stored id.** `IsEligibleAsociadoAsync(userId)` is only ever called with an id a draft, invitation, wizard state or response is stored under, and it is false when that id resolves to another user: an archived id's survivor may be eligible, but answering under the archived id would give one Asociado two ballots. Invitations are never re-pointed on merge, so the audience count and the invite send compare the live audience against the invited ids and their survivors, and the reminder sweep leaves an invitation held by a merged-away id alone.
- **Asociado ballots are eligibility-tracked but unlinkable.** The participation ledger records which eligible Asociado completed the vote and blocks a second submission, but the final CompletionTracked response stores neither `UserId` nor `InvitationId`, and the ledger stores no completion timestamp. Open results remain embargoed. After close, individual ballots may be inspected without names or timestamps; exports and the Backdoor API suppress identity and submission timestamps for all Asociado-vote rows, ordered by response id rather than submission time.
- **Tracked public wizard state is principal-bound.** Identified/CompletionTracked slug sessions continue only while the current authenticated Human matches the state’s `UserId`; logout or account switching clears that slug state before any answers render or submit. Fully Anonymous state remains principal-independent.
- **An Identified draft follows its final entry path and privacy choice.** Autosave and final submission stamp the active route and culture, so a draft resumed through a token or slug lands in the correct funnel. Choosing CompletionTracked retires the personal draft and answers when the unlinkable response is submitted.
- **CompletionTracked public participation has no correlatable start time.** A newly created unsent ledger row uses a shared epoch `CreatedAt` sentinel rather than the Human's actual public-start time.
- **Public participation rows are not emailed invitations until email is actually prepared.** A logged-in tracked public start may create a `survey_invitations` ledger row with `SentAt = null`. It is excluded from invited counts/status/reminders. If still incomplete, a later send upgrades that same row by stamping `SentAt` and queue status; if already completed through the public link, the Human is excluded rather than emailed a spent token.
- **Choice questions need at least one option with nonblank, unique values.** Create and edit reject empty option lists and blank or duplicate values for SingleChoice and MultiChoice before persistence, using the same stable-value validation as ranked and grid options. `AnswerState.IsAnswered` (`SurveyWizardFlow.cs`) counts an answer only when the option value is non-empty, and `SurveyQuestionOption.Value` defaults to `string.Empty` — an option saved with a blank `Value` makes a required question unsubmittable and cannot be named by a `ShowIf` clause.
- **Options carry no free-text flag.** `SurveyQuestionOption` is `Order` + `Value` + `Label` only, so "Other — please specify" is authored as a separate optional `ShortText` question gated by `ShowIf` on the `other` option value.
- **The anonymity chooser pre-selects Identified.** The answer view model defaults `Anonymity` to `ResponseAnonymity.Identified` and `Views/Survey/Intro.cshtml` marks that radio `checked`; the unlinked tiers are opt-in per respondent.
- **Self-service authoring runs behind an approval gate (Workgroups §11 — Surveys never references Workgroups).** Any Human with an approved profile may create/edit a Draft survey they own. The status machine is exactly: Draft → PendingApproval (author, their own Draft only); PendingApproval → Open (Board/Admin only, via **Approve and send** — opens the survey and sends invitations in one step); PendingApproval → Draft (Board/Admin only, reject, requires a non-empty note stamped into `RejectionNote`). Board/Admin retain the pre-existing direct Draft → Open path (unaffected by this gate). Direct open/close return expected guard refusals as internal results; their controller logs Warning and displays the reason, while unexpected dependency exceptions propagate. Open-and-send continues to the Send page only after a successful open; a refused open returns to Edit. Both status verbs that could walk a submission back out of the gate refuse it: `OpenAsync` and `CloseAsync` each return a refusal for a `PendingApproval` survey, so approve and reject are the only exits and `PendingApproval → Closed → Open` is not a route to an unapproved Open survey. **Approve and send** also resolves the audience to real recipients before persisting the approval, so a well-formed audience that resolves to nobody — a Team whose team is gone — fails while the survey is still in the queue rather than opening with no one invited. Approval and rejection return expected guard refusals as internal results; the Board-only queue displays those reasons with Warning logs and retains rejected note text for correction. Unexpected dependency exceptions propagate instead of being presented as review refusals. Per-recipient send failures are recorded for retry. Audience preparation remains cancellable; once invitation writes begin, the batch finishes independently of request cancellation. Every transition writes an audit entry (`AuditAction.SurveySubmittedForApproval` / `SurveyApproved` / `SurveyRejected`).
- **Single-repo ownership.** Only `SurveyRepository` touches the six `survey_*` tables; a `survey_*` table appears in no other repository.
- **Cross-domain refs are bare `Guid` FK columns** — no navigation properties, no `[Obsolete]` navs, and no cross-section EF FK constraints. Display data (creator/respondent names, recipient languages/emails) is stitched into DTOs by the service via other sections' `I<Section>Service` contracts.

## Negative Access Rules

- Code outside `SurveyRepository` (and `SurveyService` above it) **cannot** read/write `survey_*` tables; other sections **cannot** inject `ISurveyRepository` — it is `internal` (HUM0034), so cross-section injection does not compile.
- Survey code **cannot** reach into other sections' data or repositories — cross-section data comes **only** through the public interfaces listed under Architecture.
- Results, exports, and the API **cannot** expose respondent identity for CompletionTracked or Anonymous responses, or for any response belonging to an Asociado vote — `UserId`/`UserName` are populated only for Identified rows in ordinary surveys (enforced server-side regardless of API params).
- The system **cannot** store a completion timestamp for CompletionTracked responses (timing side-channel).
- Individual response submissions **cannot** be audit-logged (would re-link an anonymous answer to a time/actor).
- Logged-out public-slug requests **cannot** carry identity or a non-Anonymous tier. Logged-in public requests cannot attach identity without the respondent's explicit tier choice; CompletionTracked/Anonymous response rows cannot carry identity. `/Survey/Admin` and `/Survey/Answer` **cannot** be claimed as a public slug.
- The `LoggedInSince` audience **cannot** include GDPR-anonymized, deletion-pending, or merged users, or users in `Rejected`/`Suspended`/`AdminSuspended` state — status-walled accounts that can't reach the survey are never invited, even if they logged in after the cutoff (nobodies-collective/Humans#1099).
- A non-owner, non-Board/Admin Human **cannot** view, edit, submit, view results/export, or discover in the index any survey they didn't author — enforced by `SurveyAuthorizationHandler` on direct GET/POST by id, not by view filtering. An author **cannot** see their own results/export before their survey closes, and **cannot** see anyone else's survey at all.
- Submission guards return ordinary refusal results for missing surveys, non-authors and non-Drafts before any transition or audit. Author feedback uses the selected UI language; expected refusals log Warning with the survey identifier, while unexpected dependency failures propagate to normal error handling.
- Submit controls appear only for the owner of a Draft, including when a Board/Admin
  edits another author's draft. Ranked availability controls and admin breadcrumbs appear
  only for Board/Admin; authors keep access to their closed results and exports.

## Triggers

- When a survey is created / updated / opened / closed, an audit entry is written via `IAuditLogService.LogAsync` (`AuditAction.SurveyCreated` / `SurveyUpdated` / `SurveyOpened` / `SurveyClosed`). `SurveyUpdated` descriptions name the changed fields (audience and slug transitions spelled out; question edits collapsed to counts). The internal `SurveyChangeDescription` owns field and question comparisons; `SurveyService` still controls when the audit entry is written.
- When an author submits a Draft for approval, Board/Admin approve-and-send, or Board/Admin reject with a note, an audit entry is written (`AuditAction.SurveySubmittedForApproval` / `SurveyApproved` / `SurveyRejected`); the approve/reject entries carry the author as `relatedEntityId`/`AuditEntityTypes.User`.
- When invitations are sent, net-new `SurveyInvitation` rows are created, each email is queued via `IEmailService.SendAsync` with `SurveysEmails.SurveyInvitation` in the recipient's preferred language (`SentAt`+`LatestEmailStatus=Queued`; `Failed` on a preparation or enqueue exception), and one `AuditAction.SurveyInvitesSent` entry is logged. Preparation failures are isolated per recipient so later invitations and the audit entry still run. Audience resolution and recipient lookup remain cancellable; invitation writes, email queueing and the batch audit finish independently of request cancellation.
- When the daily `surveys-reminder` recurring job (`SendSurveyReminderJob`, cron `0 9 * * *`) runs, `SurveyReminderSender.SendDueRemindersAsync` queues one `SurveysEmails.SurveyReminder` per due invitee, stamps `ReminderSentAt`, and logs `AuditAction.SurveyReminderSent` (job actor). Preparation/enqueue failures leave that invitee unstamped for retry and do not abort later reminders; caller cancellation propagates. The job touches no repository.
- When a response is submitted, the response + answers and `Invitation.Completed` are written in one save for Identified/CompletionTracked. **No audit entry** is written for the submission.
- When the invited wizard advances past the intro, `Invitation.Started` is set; on the public path, `Survey.PublicStartedCount` is incremented.
- GDPR export follows `UserInfo.AllUserIds` for Identified responses and invitation rows retained under merged accounts, while authored surveys follow the canonical survivor ID. Frozen invitations and responses are not moved, and anonymous/completion-tracked answers remain unlinked.
- When the GDPR export runs, `SurveyService` (as `IUserDataContributor`) contributes the user's **Identified** responses under `SurveyService.SurveyResponses`, plus the surveys they authored (`CreatedByUserId`, any status, Drafts included) under `SurveyService.AuthoredSurveys` — authoring is open to every approved Human, so a member's own surveys and any Board rejection note are their personal data — plus their invitation ledger under `SurveyService.SurveyInvitations`. Most responses are not Identified, so for someone who was only invited, or who answered under completion tracking, the ledger row is the only record of theirs the section holds; it carries invitation-side timestamps only (`CreatedAt`, `SentAt`, `ReminderSentAt`) and the `Started`/`Completed` flags, which say nothing about when an anonymous answer arrived.
- When Article 17 erasure runs, `EraseForUserAsync` deletes the user's `SurveyInvitation` rows and severs their Identified responses from the person (`UserId`/`InvitationId` dropped, `Anonymity` forced to `Anonymous`) — the answers themselves survive as an anonymous data point in the survey's results (Art. 17(3)(b)), which is what `ErasureDeclaration` names as partial retention for `SurveyService.SurveyResponses`. It also drops authorship on the surveys they wrote (`CreatedByUserId` to `Guid.Empty`, `RejectionNote` cleared) while the survey and its questions survive as the association's own record — partial retention for `SurveyService.AuthoredSurveys`.
- The invitation rows that same erasure deletes are exported, so `ErasureDeclaration` declares them too — full erasure, not partial retention, for `SurveyService.SurveyInvitations`.
- On an account merge, `ReassignAsync` (`IUserMerge`) moves `Survey.CreatedByUserId` from the
  eliminated account onto the survivor and stamps `UpdatedAt`. Without it the author-scoped
  index would hide the survivor's own surveys from them, and the authorization handler would
  refuse their drafts.

## Cross-Section Dependencies

- **Audit actions:** declared on `SurveysAuditActions` (`src/Sections/Humans.Surveys/SurveysAuditActions.cs`), one `AuditActionDefinition` each, written through `IAuditLogService`: `SurveyCreated`, `SurveyUpdated`, `SurveyOpened`, `SurveyClosed`, `SurveyInvitesSent`, `SurveyReminderSent`, `SurveySubmittedForApproval`, `SurveyApproved`, `SurveyRejected`, `SurveyResponsesExported`.
- **Users/Identity:** `IUserService` — resolve active-member ids and `LoggedInSince`-audience ids (`UserInfo.LastLoginAt`), and creator/respondent/recipient display names + preferred languages.
- **Teams:** `ITeamService` — `Team`-audience member ids and team display data.
- **Consent:** `IConsentService.GetConsentReviewsAsync` — the Consent Coordinator's approval, which gates the approved-members audience (it does not gate Asociado voting).
- **Governance:** `IGovernanceService.GetMembershipTierAsync` / `GetMembershipTiersAsync` — Asociado voting eligibility and the `Asociados` audience.
- **Tickets:** `ITicketService` — `TicketHolders`-audience recipient ids.
- **Shifts:** `IShiftView` — `ShiftParticipants`-audience recipient ids.
- **Users/Identity:** `IUserEmailService` — effective notification target email per recipient when queueing invite/reminder mail.
- **Email:** Surveys owns its two templates — `SurveysEmails` (internal) builds each `EmailMessage` from Surveys' own `Surveys_Email_*` keys in `SurveysResource`, rendered in the recipient's culture via `CultureScope`; `SurveysEmailPreviews` (`IEmailPreviewContributor`, registered in `Section.Register`) lists them at `/Email/EmailPreview`. Email supplies transport only — `IEmailService.SendAsync` (queued through the email outbox in production), plus `IEmailPreviewService` for the preview-email-to-self page (`memory/architecture/email-templates-live-in-sender.md`, peterdrier/Humans#1651).
- **Files:** `IFileStorage` — public objects under `uploads/surveys/` for Information-item images.
- **GoogleIntegration:** `IGoogleTranslationService` — the builder's "Save + translate missing" pre-fills blank cultures from the default culture (Cloud Translation; dev stub returns `[xx]`-prefixed text). Fills blanks only — authored text is never overwritten.
- **Audit Log:** `IAuditLogService.LogAsync` — survey lifecycle + send/reminder events (never individual submissions).
- **Data Protection:** `IDataProtectionProvider` via `ISurveyInviteTokenProvider` and
  `SurveyPreviewTokenProvider` — time-limited, tamper-evident invitation and preview tokens with
  distinct purposes (`/Survey/Answer?t={token}`).
- **GDPR:** implements `IUserDataContributor` to export the user's Identified survey responses under `SurveyService.SurveyResponses` and the surveys they authored under `SurveyService.AuthoredSurveys` and their invitation ledger under `SurveyService.SurveyInvitations`, and to erase all three on Article 17 request (invitation deleted, response demoted to Anonymous, authorship blanked to `Guid.Empty` — see Triggers).

## Architecture

**Owning services:** `SurveyService`
**Owned tables:** `surveys`, `survey_questions`, `survey_question_options`, `survey_invitations`, `survey_responses`, `survey_answers`
**Status:** (A) Migrated. Everything but `Section`, `SurveysResource`, `Contracts/` and `Jobs/` is `internal` (HUM0034). `Contracts/` is entirely public: the enums, the `SurveyDefinitionSnapshot` and `Contracts/SurveyReadModels.cs` DTOs, `SurveyResponsesMarkdownBuilder`, `ISurveyReminderSender.SendDueRemindersAsync`, and `ISurveyAnalysisRead` (the Backdoor API's read surface — see Cross-section read interface below). `Jobs/SendSurveyReminderJob.cs` is public under the HUM0034 `Jobs/` carve-out because the Shell names the concrete type when it registers and schedules it.

- `SurveyService` lives in `Humans.Surveys.Services` and never imports `Microsoft.EntityFrameworkCore`. Implements `ISurveyService`, `IUserDataContributor` and `IUserMerge`. The internal `SurveyDefinitionValidation` owns question configuration, branching, vote-mode and frozen ranked-definition rules; create and update share the same ordered definition-validation chain. Authorization and persistence remain in `SurveyService`. The internal `SurveyQuestionResults` owns pure per-question counts, distributions and ranked-result preparation; response loading, scope selection, embargo and identity disclosure remain in `SurveyService`. The internal `SurveyImageUploads` owns Information-image validation, upload preparation and best-effort cleanup for both create and update; `SurveyService` retains authorization, invokes definition validation and verifies persistence after failures. It reuses the existing file storage and logger without a DI registration or contract change. The internal `SurveyReminderSender` implements the existing `ISurveyReminderSender` contract and owns the daily reminder batch.
- `ISurveyRepository` (impl `src/Sections/Humans.Surveys/Data/SurveyRepository.cs`, `internal sealed`) is the only code path that touches the owned tables above via `DbContext`. Registered as Singleton; uses `IDbContextFactory<SurveysDbContext>` (per-section DbContext, nobodies-collective/Humans#858; baseline migration `20260715105933_BaselineSurveys` under `Data/Migrations/`) for per-call scoped contexts.
- **Aggregate-local navs kept:** `Survey.Questions ↔ SurveyQuestion.Survey`, `SurveyQuestion.Options ↔ SurveyQuestionOption.Question`, `SurveyResponse.Answers ↔ SurveyAnswer.Response`. All within Survey-owned tables, so these `.Include`s are legal inside the repository.
- **Decorator decision — no caching decorator.** Admin-authored, low-traffic, per-invitee writes — not a hot bulk-read path (Feedback/Issues rationale). Registered as a plain Scoped service.
- **Cross-domain navs — none.** Survey references Users/Teams by **bare `Guid` FK columns only** (`memory/architecture/no-cross-section-ef-joins.md`), with **no `[Obsolete]` navs and no cross-section EF FK constraints**. The service resolves display data via the cross-section read interfaces and returns DTOs.
- **Cross-section calls — the public interfaces this section consumes:** `IUserService`, `ITeamService`, `IConsentService`, `IGovernanceService`, `ITicketService`, `IShiftView`, `IUserEmailService`, `IEmailService`, `IEmailPreviewService`, `IAuditLogService`, `IGoogleTranslationService`, `IFileStorage`, `IDataProtectionProvider` (via `ISurveyInviteTokenProvider` and `SurveyPreviewTokenProvider`).
- **Architecture test** — `tests/Humans.Surveys.Tests/SurveysArchitectureTests.cs` pins the section shape. HUM0025 enforces single-owner table access; cross-section repository injection does not compile, `ISurveyRepository` being internal — a per-consumer allow-list test would only assert absence ([`no-tests-for-absences`](../../../../memory/architecture/no-tests-for-absences.md)).

### Cross-section read interface

`ISurveyAnalysisRead` (public, in `Contracts/`): survey list, one survey's resolved question graph (`GetDefinitionAsync` → `SurveyDefinitionSnapshot`), the raw per-response export, and the per-question aggregates. Read-only — a survey is authored only in the admin UI, never over the API. Its sole consumer is `Humans.Backdoor`'s `BackdoorSurveysController` behind `/api/backdoor/surveys`. The section's only other public behaviour surface is `ISurveyReminderSender`, consumed by `Jobs/SendSurveyReminderJob`. There is deliberately **no public `ISurveyService` contract**: the empty public read contract was deleted (no other section consumed it); the internal `ISurveyService` remains — the machine API's needs are a different shape and are served by `ISurveyAnalysisRead`.

Response exports (admin CSV/JSON and Backdoor analysis) record `SurveyResponsesExported` with the requesting human, survey id and row count in the owner service. Missing surveys and embargoed open Asociado votes return no export and no disclosure audit. Privacy shaping and results authorization remain unchanged.
