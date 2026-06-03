# Survey Section — Design Spec

**Date:** 2026-06-03
**Status:** Draft — mini-design, pending Peter's review before implementation
**Owner:** Peter Drier
**Context:** Captured from a design dialogue (this repo has no GitHub Issues tracker, so the spec lands here in `docs/superpowers/specs/` to be picked up later). Grounded in a codebase audit of the Email outbox, Campaigns, Mailer audiences, Hangfire jobs, i18n, anonymous-token flows, and the section/authorization patterns.

## 1. Problem / goal

The collective needs a first-party **Survey** tool so the Board/Admin can ask the membership (and ticket buyers, teams, etc.) questions and collect structured answers — without reaching for an external SaaS (Google Forms / Typeform) that would put member PII outside the system and outside GDPR control.

A survey author must be able to:

- **Author a survey** — title, intro, a set of typed questions (choice / text / rating), with **conditional branching** (show question B only if answer A = X).
- **Translate it** — the same survey rendered in any of the supported UI languages (`en/es/de/fr/it/ca`).
- **Target an audience** — all users, ticket holders, a team, a role, etc. — reusing the existing audience machinery rather than re-inventing it.
- **Send individual email invites** — one personalised, tokenised link per recipient, delivered through the existing Email outbox.
- **Let people answer anonymously** — the first step of the answering wizard offers a privacy choice (when the author allows it).
- **Chase non-responders** — an automatic reminder email a week later to people who were invited but haven't completed it.
- **Read the results** — per-question aggregates + (for identified responses) per-respondent detail.

This is a new **vertical section** (`Survey`), born compliant with current architecture (design-rules §15), owning its own tables and namespace.

## 2. Decisions taken in the design dialogue

| Question | Decision |
|---|---|
| Where this spec lives | This repo has **no Issues tracker** → committed as a dated design doc under `docs/superpowers/specs/`. Not a GitHub issue. |
| Section shape | New top-level **Survey** section. URL `/Survey` (+ `/Survey/Admin`), namespace `Humans.Application.Survey`, owned tables `surveys`, `survey_questions`, `survey_question_options`, `survey_invitations`, `survey_responses`, `survey_answers`. |
| **Anonymity** | **Author opt-in flag per survey.** If the author allows anonymous responses, the wizard's first step offers the respondent **three choices** (see §4). If the author does **not** allow it, responses are always **Identified** and the survey is invite-only (no public/anonymous path). |
| **Audience / targeting** | **Reuse, extend Campaigns/Mailer.** Recipient set is resolved through the existing **`IMailerAudience`** audience framework (all-users / ticket-holders / team / role already exist there); per-recipient invite tracking mirrors **`CampaignGrant`** (token + send-status + completion). |
| **Delivery** | **Reuse the Email outbox** (`IEmailService` → `email_outbox_messages`). New `IEmailMessageFactory` templates `SurveyInvitation` + `SurveyReminder`, localised in the recipient's `PreferredLanguage`. No new transport. |
| **Reminders** | Hangfire recurring job (daily tick) enqueues a reminder for invitations that are **un-completed**, **sent ≥ 7 days ago**, and **not yet reminded**. One reminder per invitation. |
| **Question types** | Single-choice, multi-choice, free text (short/long), rating/scale — **plus conditional branching** (skip logic). |
| **Multi-language content** | New **section-local** `LocalizedText` value object (jsonb `{culture: string}`) for all authored strings — the app has no per-language *content* storage today (resx is UI-chrome only). See §6 + Open Questions for the "promote to shared" question. |
| Authoring/sending authority | `AdminOrBoard` policy (existing). A dedicated `SurveyAdmin` role is a follow-up if delegation is needed — not in v1. |

## 3. Data model

All cross-domain FKs are **FK-only**, `[Obsolete]`-marked navs (design-rules §6c); the repo never `.Include()`s them. Aggregate-local navs (Survey↔Question↔Option, Response↔Answer) are kept and `.Include()`-able.

### `Survey` (table `surveys`)

| Field | Type | Notes |
|-------|------|-------|
| `Id` | Guid | PK |
| `Title` | `LocalizedText` (jsonb) | Per-culture display title |
| `Intro` | `LocalizedText?` | Wizard landing copy |
| `ThankYou` | `LocalizedText?` | Post-submit copy |
| `DefaultCulture` | string (max 10) | Fallback culture when a respondent's language has no translation |
| `Cultures` | string[] (jsonb) | Cultures the author has provided translations for |
| `AllowAnonymous` | bool | Gates the §4 three-choice step and the public/anonymous path |
| `Status` | enum `SurveyStatus` | `Draft` / `Open` / `Closed` |
| `OpensAt` | Instant? | Optional scheduled open |
| `ClosesAt` | Instant? | Optional auto-close; after this, the wizard rejects new responses |
| `AudienceKey` | string? | Key of the reused `IMailerAudience` (null = manual/none) |
| `PublicSlug` | string? (max 80) | Set when a shareable public link is enabled; null = invite-only |
| `CreatedByUserId` | Guid | FK → User, **FK only**, `[Obsolete]` nav |
| `CreatedAt` / `UpdatedAt` | Instant | |

**Aggregate-local navs:** `Survey.Questions`. **Indexes:** `Status`, `PublicSlug` (unique, filtered non-null).

### `SurveyQuestion` (table `survey_questions`)

| Field | Type | Notes |
|-------|------|-------|
| `Id` | Guid | PK |
| `SurveyId` | Guid | FK → Survey, cascade |
| `PageNumber` | int | Wizard step grouping; questions on the same page render together |
| `Order` | int | Order within the page |
| `Type` | enum `SurveyQuestionType` | `SingleChoice` / `MultiChoice` / `ShortText` / `LongText` / `Rating` |
| `Prompt` | `LocalizedText` | The question text |
| `HelpText` | `LocalizedText?` | Optional sub-text |
| `IsRequired` | bool | Enforced only when the question is **visible** after branching |
| `RatingMin` / `RatingMax` | int? | For `Rating` (e.g. 1–5, 0–10/NPS) |
| `RatingMinLabel` / `RatingMaxLabel` | `LocalizedText?` | Scale endpoint labels |
| `ShowIf` | `BranchCondition?` (jsonb) | Branching rule — see §5. Null = always shown. |

**Aggregate-local navs:** `SurveyQuestion.Options`. **Indexes:** `(SurveyId, PageNumber, Order)`.

### `SurveyQuestionOption` (table `survey_question_options`)

| Field | Type | Notes |
|-------|------|-------|
| `Id` | Guid | PK |
| `QuestionId` | Guid | FK → SurveyQuestion, cascade |
| `Order` | int | |
| `Value` | string (max 100) | Stable machine value (branching + aggregation key); not localised |
| `Label` | `LocalizedText` | Display label per culture |

### `SurveyInvitation` (table `survey_invitations`) — mirrors `CampaignGrant`

One row per targeted recipient. This is the unit the reminder job and completion tracking operate on.

| Field | Type | Notes |
|-------|------|-------|
| `Id` | Guid | PK |
| `SurveyId` | Guid | FK → Survey, cascade |
| `UserId` | Guid | FK → User, **FK only**, `[Obsolete]` nav |
| `SentAt` | Instant? | When the invite was enqueued to the outbox |
| `LatestEmailStatus` | `EmailOutboxStatus?` | Mirror of `CampaignGrant.LatestEmailStatus` |
| `ReminderSentAt` | Instant? | Set when the one reminder is enqueued |
| `CompletedAt` | Instant? | Set when this invitee submits as **Identified** or **Completion-tracked** (suppresses reminder). Never set for **Fully anonymous** (§4). |
| `CreatedAt` | Instant | |

**Indexes:** unique `(SurveyId, UserId)`; `(SurveyId, CompletedAt, SentAt)` for the reminder sweep.

> **Token, not stored:** the per-recipient link carries an ASP.NET **Data Protection** token (purpose `"survey-invitation"`) protecting the payload — the same pattern as the unsubscribe token in `GuestController`. No raw token column; the token resolves server-side to the invitation (and thus the user). See §7.1 for why this is a **keyed signature** over the user GUID, not a reversible obfuscation.

### `SurveyResponse` (table `survey_responses`)

| Field | Type | Notes |
|-------|------|-------|
| `Id` | Guid | PK |
| `SurveyId` | Guid | FK → Survey, cascade |
| `InvitationId` | Guid? | FK → SurveyInvitation (SetNull). Set when answered via an invite link; null for public/anonymous-link responses. |
| `UserId` | Guid? | FK → User, **FK only**, `[Obsolete]` nav. **Null unless the respondent chose Identified.** |
| `Anonymity` | enum `ResponseAnonymity` | `Identified` / `CompletionTracked` / `Anonymous` (see §4) |
| `Culture` | string (max 10) | Language the wizard was answered in |
| `SubmittedAt` | Instant | |

**Aggregate-local navs:** `SurveyResponse.Answers`. **Indexes:** `SurveyId`, `(SurveyId, UserId)`.

### `SurveyAnswer` (table `survey_answers`)

| Field | Type | Notes |
|-------|------|-------|
| `Id` | Guid | PK |
| `ResponseId` | Guid | FK → SurveyResponse, cascade |
| `QuestionId` | Guid | FK → SurveyQuestion (Restrict — can't delete a question with answers) |
| `SelectedOptionValues` | string[] (jsonb) | For choice questions — the stable `Option.Value`s |
| `TextValue` | string? (max 4000) | For text questions |
| `RatingValue` | int? | For rating questions |

**Indexes:** `(ResponseId)`, `(QuestionId)`.

## 4. Anonymity — author opt-in, three respondent tiers

**Author flag `Survey.AllowAnonymous`** governs the whole model:

- **`AllowAnonymous = false`** → survey is **invite-only and always Identified**. No public slug, no anonymity step. The wizard requires a valid invitation token (or a logged-in targeted user). `SurveyResponse.UserId` is always set.
- **`AllowAnonymous = true`** → the wizard's **first step** presents three choices (`ResponseAnonymity`):

| Choice | `Response.UserId` | `Invitation.CompletedAt` | Effect |
|--------|-------------------|--------------------------|--------|
| **Identified** | invitee's user id | set | Answers attributable to the person; counts as done; no reminder. |
| **Completion-tracked** | `null` | **set** | "Mark me as done but don't attach my answers to me." Answers are anonymous; participation is recorded so they're **not chased** by the reminder and counted in the response rate. |
| **Fully anonymous** | `null` | **not set** | No trace that *this* person answered. **Trade-off:** because completion isn't recorded, a fully-anonymous invitee **may still receive the one reminder.** This is documented to the respondent on the choice step. |

Public (non-invited) responses via `PublicSlug` are always **Anonymous** (no invitation to attach).

**GDPR consequence:** only `Identified` responses are personal data → included in the data-export contributor (§11) and deletable. `CompletionTracked` and `Anonymous` responses carry no user link and are **not** exportable/attributable by design.

## 5. Conditional branching

`SurveyQuestion.ShowIf` is a nullable `BranchCondition` value object (stored jsonb):

```
BranchCondition {
  Combine: "All" | "Any"          // AND / OR across clauses
  Clauses: [
    { QuestionId, Operator, OptionValues[] }   // Operator: Is | IsNot | Answered | NotAnswered
  ]
}
```

- A condition may only reference questions on an **earlier page** (no forward/cyclic references — validated at author-save time).
- The wizard is **page-based**. On each page submit, the server records answers and computes the **next visible page** by evaluating the `ShowIf` of each downstream question; pages whose every question is hidden are skipped.
- **Required-ness only applies to visible questions** — a hidden required question never blocks submission.
- Evaluation is **server-side** (no trust in client state); the same evaluation runs at final submit to reject answers to questions that should have been hidden.

Authoring UI shows branching as a per-question "Show this question only if…" rule builder; v1 supports choice-question predicates only (text/rating are not valid branch sources).

## 6. Multi-language content

The app today localises only **UI chrome** via `IStringLocalizer` + `.resx` (cultures `en/es/de/fr/it/ca`); there is **no per-language content storage** in the domain. Surveys introduce one:

- **`LocalizedText`** — a section-local value object wrapping `IReadOnlyDictionary<string,string>` (culture → text), persisted as a single **jsonb** column (Postgres). Used for every authored string (title, intro, thank-you, prompts, help, option labels, scale labels).
- **Resolution:** render in the respondent's culture → fall back to `Survey.DefaultCulture` → fall back to any present culture. The respondent's culture is `User.PreferredLanguage` for logged-in/invited users, or a **language picker on the wizard's first step** for anonymous/public respondents (defaulting to the request culture from the existing `CustomRequestCultureProvider`).
- **Authoring:** the author edits one culture at a time with a culture switcher; `Survey.Cultures` records which translations exist. Missing translations are flagged but don't block publishing (fallback covers them).
- **Emails** (`SurveyInvitation`/`SurveyReminder`) render the subject/body in the recipient's `PreferredLanguage`, body links to the survey, which itself opens in that culture.

> **Decision flagged for Peter (Open Q):** keep `LocalizedText` **Survey-owned** for now (reuse-first: don't build a shared abstraction before a second consumer exists), vs. promote it to a shared Domain value object usable by Events/Camps later. Spec assumes section-local; promotion is a later refactor.

## 7. Distribution & reminders (reuse Campaigns/Mailer + Email outbox)

**Audience resolution** reuses the Mailer **`IMailerAudience`** framework — the existing `ComputeMemberUserIdsAsync` implementations already cover *all-users*, *ticket-holders* (`HasTicketAudience`), team/shift cohorts, etc. The Survey author picks an `AudienceKey`; the send flow resolves it to a user-id set the same way audience sync does today.

> **Reuse boundary flagged for Peter (Open Q):** `IMailerAudience` lives in the Mailer section and is coupled to MailerLite group names. Cross-section reuse should go through a **read interface** (e.g. an `IAudienceResolver` / `IMailerServiceRead.ResolveAudienceAsync(key)`) rather than Survey importing Mailer internals — exact surface to be settled at implementation, consistent with the hard-rules cross-section pattern. Fallback if that proves heavy: Survey resolves cohorts directly via the same cross-section reads the audiences use (`ITicketServiceRead`, `ITeamService`, `IUserServiceRead`).

**Send flow** (`SurveyService.SendInvitesAsync`, mirrors a Campaign wave):
1. Resolve audience → user-id set.
2. Create one `SurveyInvitation` per user (idempotent on `(SurveyId, UserId)`).
3. For each, build a tokenised link (`/Survey/Answer?t={token}` — see §7.1) and enqueue a `SurveyInvitation` email via `IEmailService`, localised to `PreferredLanguage`. Stamp `SentAt` + `LatestEmailStatus`.

### 7.1 Invite token — identify, don't authenticate, don't let randoms spoof

The link must let us **identify the recipient** (recover their user GUID server-side, so the response can be attributed when they choose *Identified*, and so completion/reminders track the right person) while satisfying two hard constraints:

- **Not a login.** The token grants exactly one capability — open *this* survey as *this* invitee — and never establishes a session, cookie, or any access to other pages. It is purpose-scoped (`"survey-invitation"`), like the unsubscribe token's `CommunicationPreferences` purpose. A leaked link exposes only that one survey context.
- **Unforgeable.** A random who copies someone's user GUID (or guesses one) **must not** be able to construct a valid link.

**Mechanism — keyed signature, not a reversible hash.** The token is an ASP.NET **Data Protection** payload (`IDataProtectionProvider.CreateProtector("survey-invitation").Protect(...)`) carrying the `InvitationId` (which maps 1:1 to `SurveyId` + user GUID). Data Protection encrypts **and MACs** the payload with a server-held key, so the token is effectively a signed, opaque encoding of the GUID that **cannot be produced without the server key**.

> **Why not rot13 / a plain hash of the GUID:** any reversible obfuscation (rot13, base64, XOR) or an *unkeyed* hash is trivially reproducible — anyone with a target's GUID can compute the same string and spoof the invite. The anti-spoof property comes specifically from the **secret key** in the MAC/encryption, not from the encoding being unreadable. So we use the keyed Data Protection token (the codebase's existing primitive for exactly this), not a homegrown obfuscation. Same reason we don't roll our own here: the magic-link and unsubscribe flows already depend on this primitive.

Optional hardening (cheap, recommended): give the token a **lifetime** tied to the survey's open window via `ITimeLimitedDataProtector`, so stale links stop resolving after `ClosesAt`.

**Reminder job** (`SendSurveyReminderJob : IRecurringJob`, daily cron):
- Selects invitations where `Survey.Status == Open`, `CompletedAt is null`, `SentAt <= now - 7d`, `ReminderSentAt is null`.
- Enqueues one `SurveyReminder` email; stamps `ReminderSentAt`. One reminder per invitee, ever.

Both honour the existing outbox pause flag and unsubscribe/Marketing rules already enforced by the Email/Mailer stack (a survey **invite** is transactional, not marketing — but recipients still resolve through the same audience exclusions when an `IMailerAudience` is used).

## 8. Wizard (the answering flow)

`/Survey/Answer?t={token}` (invited) or `/Survey/{publicSlug}` (public) — both `[AllowAnonymous]`, like `WelcomeController`/`GuestController`.

1. **Step 0 — intro + privacy + language.** Renders `Survey.Intro`. If `AllowAnonymous`, shows the three-choice privacy selector (§4) with the reminder trade-off note. Anonymous/public path shows a language picker.
2. **Question pages.** One page at a time; required-visible questions validated server-side; branching evaluated on each advance.
3. **Submit.** Writes `SurveyResponse` (+ `SurveyAnswer`s) with the chosen `Anonymity`; sets `Invitation.CompletedAt` for Identified/Completion-tracked. Renders `Survey.ThankYou`.

**Re-entry / edit:** an invited respondent can reopen their in-progress/submitted response via the same token within the open window (Data-Protection token, no login) — matching the unsubscribe-token "scoped link" pattern. Anonymous responses are fire-and-forget (no edit link).

**Admin views** (`/Survey/Admin`, `AdminOrBoard`):
- **Index** — list of surveys with status, response count / invited count (response rate).
- **Builder** — create/edit survey, pages, questions, options, translations, branching rules; pick audience; toggle `AllowAnonymous` / public slug; open/close.
- **Send** — preview audience size, send invites, see per-invite delivery status (reuse the Campaign wave status surface).
- **Results** — per-question aggregates (counts/%, rating distributions, text answer list); for `Identified` responses, a per-respondent drill-down. Export to CSV.

## 9. Authorization

| Action | Allowed by |
|--------|-----------|
| Create / edit / send / view results | `AdminOrBoard` policy |
| Answer (invited) | Anyone with a valid invitation token (`[AllowAnonymous]`) |
| Answer (public) | Anyone, when `PublicSlug` set and `AllowAnonymous` (`[AllowAnonymous]`) |
| Reminder job | system (Hangfire) |

`/Survey/Answer` + `/Survey/{slug}` are exempt from `MembershipRequiredFilter` (like `Welcome`/`Guest`/magic-link). A dedicated `SurveyAdmin` role is **out of v1** (follow-up if Board wants to delegate authoring without full Admin/Board).

## 10. Cross-section dependencies

| Dependency | Used for |
|------------|----------|
| `IMailerServiceRead` / `IMailerAudience` (read surface — §7 Open Q) | Resolve `AudienceKey` → recipient user-id set |
| `IUserServiceRead.GetUserInfosAsync` | Recipient names + `PreferredLanguage` for invite/reminder emails and results display |
| `IEmailService` + new `IEmailMessageFactory.SurveyInvitation/SurveyReminder` | Invite + reminder delivery through the outbox |
| `IAuditLogService.LogAsync` | Survey lifecycle + send audit entries |
| `IDataProtectionProvider` | Tokenised invite/edit links (purpose `"survey-invitation"`) |
| `IUserDataContributor` (implemented by `SurveyService`) | GDPR export of a user's **Identified** responses (`GdprExportSections.Survey`, new) |

`Survey` sits **above** Users / Profiles / Tickets / Teams / Mailer / Email — it calls into them, never the reverse.

## 11. GDPR

- **Export:** `SurveyService` implements `IUserDataContributor`; a user's export includes their `Identified` responses (survey title, submitted-at, their answers). `CompletionTracked` / `Anonymous` responses are unattributable and excluded.
- **Deletion / anonymisation:** on user deletion, `SurveyInvitation.UserId` and `Identified` `SurveyResponse.UserId` follow the section's standard FK-SetNull/anonymise path; the response content for already-anonymous tiers is untouched (no PII).
- **Consent framing:** survey invites are operational membership communication, not marketing; the privacy choice + clear intro copy is the lawful-basis surface for storing identified answers.

## 12. Architecture (per design-rules §15)

- **Owning service:** `SurveyService` (`Humans.Application.Services.Survey`). Application-layer; never imports `Microsoft.EntityFrameworkCore`. Calls its own repo + other sections' **service interfaces** only.
- **Repository:** `ISurveyRepository` (`Humans.Infrastructure/Repositories/Survey/SurveyRepository.cs`). Owns the SQL surface; `IDbContextFactory<HumansDbContext>` per call.
- **Owned tables:** `surveys`, `survey_questions`, `survey_question_options`, `survey_invitations`, `survey_responses`, `survey_answers`.
- **Job:** `SendSurveyReminderJob` in `Humans.Infrastructure/Jobs/`, resolves `SurveyService` via DI, goes through the service interface (no repo from the job).
- **Cross-domain navs `[Obsolete]`-marked:** `Survey.CreatedByUser`, `SurveyInvitation.User`, `SurveyResponse.User`. Repo never `.Include()`s them; the service stitches display data via cross-section reads.
- **Caching:** none in v1 (admin-authored, read-on-demand; results aggregation is a per-survey query at 500-user scale). Add a decorator only if a hot read emerges.
- **Status:** (A) born §15-compliant.
- **Architecture tests:** add `SurveyArchitectureTests` pinning no-EF-in-Application, single-repo table ownership, and cross-section-via-interface.

## 13. Out of scope (v1)

- Response **quotas / caps**, randomised question order, A/B variants.
- Piped text / answer interpolation into later prompts.
- File-upload answer type.
- Multiple reminders / configurable reminder cadence (v1 = exactly one, at 7 days).
- SMS / push delivery — email only.
- Realtime results dashboards / charts beyond basic aggregates + CSV.
- A `SurveyAdmin` delegation role.
- Cross-survey respondent identity / longitudinal linking.

## 14. Open questions / decisions for Peter

1. **`LocalizedText` scope** — Survey-owned value object now (assumed), vs. promote to a shared Domain primitive for future Events/Camps localisation. (§6)
2. **Audience reuse surface** — introduce `IMailerServiceRead.ResolveAudienceAsync(key)` (or `IAudienceResolver`) as the cross-section read, vs. Survey resolving cohorts directly via `ITicketServiceRead`/`ITeamService`/`IUserServiceRead`. The `IMailerAudience` set is MailerLite-coupled today. (§7)
3. **Fully-anonymous reminder trade-off** — confirm the documented behaviour (a fully-anonymous invitee may get the one reminder, because we record no completion for them). Alternative would be a privacy-leaking "answered" bit — rejected here. (§4)
4. **Public slug** — ship the public-link path in v1, or invite-only first and add public links later?
5. **Branch sources** — v1 restricts branching predicates to choice questions. Confirm rating/text branch sources aren't needed initially. (§5)

## 15. Acceptance criteria

- An `AdminOrBoard` user can author a multi-question survey with single/multi-choice, text, and rating questions, translate it into ≥2 cultures, and add a branching rule that hides a question based on a prior choice answer.
- Publishing with an `AudienceKey` creates one `SurveyInvitation` per resolved recipient and enqueues a localised invite email per recipient through the outbox.
- A recipient opens the tokenised link without logging in; when `AllowAnonymous`, the first step offers Identified / Completion-tracked / Fully-anonymous and (for public/anon) a language picker.
- Branching: a question whose `ShowIf` is unmet is skipped and, even if required, does not block submission; the server rejects answers to questions that should have been hidden.
- An identified submission sets `Response.UserId` and `Invitation.CompletedAt`; a completion-tracked submission sets only `CompletedAt`; a fully-anonymous submission sets neither user link nor completion.
- Seven days after send, un-completed invitations receive exactly one reminder email; completed (Identified/Completion-tracked) invitees receive none.
- Results view shows per-question aggregates and a per-respondent drill-down limited to Identified responses; CSV export works.
- A user's GDPR export includes their Identified responses and omits anonymous/completion-tracked ones.
- Architecture tests pin: no EF in `Humans.Application.Survey`, `survey_*` tables owned solely by `SurveyRepository`, all cross-section access via service interfaces.
