# Survey Section — Design Spec

**Date:** 2026-06-03
**Status:** Decisions resolved (2026-06-04 design dialogue) — v1 scope locked (§15). Implementation plan to be produced and reviewed by Peter **before** any code is written; build is one chunk / one branch / one PR (no incremental sub-releases).
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
| **Assisted translation** | **Reuse the existing Google service-account** to add Google **Cloud Translation** as a "pre-fill translations" button — author writes one culture, machine-translates the rest, **then reviews/edits**. Cost is negligible (~16k chars/survey, inside the 500k-chars/month free tier). See §6.1. |
| Authoring/sending authority | `AdminOrBoard` policy (existing). A dedicated `SurveyAdmin` role is a follow-up if delegation is needed — not in v1. |

## 3. Data model

All cross-domain references are **bare `Guid` FK columns** — **no navigation property and no cross-section EF FK constraint** (design-rules §6c, taken literally; the clean `FeedbackReport.AgentConversationId` precedent, *not* the `[Obsolete]`-nav grandfathered debt). A new section is not born with `[Obsolete]` anything. The service resolves cross-section data via `IUserServiceRead`/`ITeamServiceRead` into DTOs/view models — never onto entities. Aggregate-local navs (Survey↔Question↔Option, Response↔Answer) are kept and `.Include()`-able.

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
| `PublicSlug` | string? (max 80) | Set when a shareable public link is enabled (requires `AllowAnonymous`); null = invite-only. Unique, filtered non-null. Reserved words (`Admin`, `Answer`) rejected at save to avoid route collisions. |
| `PublicStartedCount` | int | Count of public (slug) visitors who began the questionnaire — the slug-path "started" funnel number. Anonymous visitors have no per-person anchor, so this is a plain counter (inflated by reloads; rough by design). Default 0. |
| `CreatedByUserId` | Guid | Creator user id — **bare `Guid` column**, no nav, no cross-section FK constraint; resolve via `IUserServiceRead` |
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
| `UserId` | Guid | Invitee user id — **bare `Guid` column**, no nav, no cross-section FK constraint; resolve via `IUserServiceRead` |
| `SentAt` | Instant? | When the invite was enqueued to the outbox |
| `LatestEmailStatus` | `EmailOutboxStatus?` | Mirror of `CampaignGrant.LatestEmailStatus` |
| `ReminderSentAt` | Instant? | Set when the one reminder is enqueued |
| `Completed` | bool | Set true when this invitee submits as **Identified** or **Completion-tracked** (suppresses reminder; counts toward response rate). **No completion timestamp is stored** — a precise time would correlate with a completion-tracked/anonymous response's submit time and unmask the respondent (§4, §3 privacy note). Stays false for **Fully anonymous**. |
| `Started` | bool | Set true when this invitee first advances past the intro into the questionnaire — the "started" side of the funnel for the link path. **No timestamp** (same privacy rule as `Completed`). Note: `Started=true`/`Completed=false` is indistinguishable from abandonment, so an anonymous finish leaves no "finished" trace on the invitation. |
| `CreatedAt` | Instant | |

**Indexes:** unique `(SurveyId, UserId)`; `(SurveyId, Completed, SentAt)` for the reminder sweep.

> **Token, not stored:** the per-recipient link carries an ASP.NET **Data Protection** token (purpose `"survey-invitation"`) protecting the payload — the same pattern as the unsubscribe token in `GuestController`. No raw token column; the token resolves server-side to the invitation (and thus the user). See §7.1 for why this is a **keyed signature** over the user GUID, not a reversible obfuscation.

### `SurveyResponse` (table `survey_responses`)

| Field | Type | Notes |
|-------|------|-------|
| `Id` | Guid | PK |
| `SurveyId` | Guid | FK → Survey, cascade |
| `InvitationId` | Guid? | FK → SurveyInvitation (SetNull). Set when answered via an invite link; null for public/anonymous-link responses. |
| `UserId` | Guid? | Respondent user id — **bare `Guid?` column**, no nav, no cross-section FK constraint. **Null unless the respondent chose Identified.** |
| `Anonymity` | enum `ResponseAnonymity` | `Identified` / `CompletionTracked` / `Anonymous` (see §4) |
| `InputMethod` | enum `SurveyInputMethod` | How the response was entered: `UserSpecificLink` (tokenised invite) or `Slug` (public link). Lets the funnel split finishes by method. v1 records `UserSpecificLink` unless the public-slug path is in scope (open). |
| `Culture` | string (max 10) | Language the wizard was answered in |
| `SubmittedAt` | Instant? | **Null while an in-progress draft**; set at final submit. Only **Identified** drafts are persisted (resumable, §8); completion-tracked/anonymous are session-only until submit. |

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

| Choice | `Response.UserId` | `Response.InvitationId` | `Invitation.Completed` | Effect |
|--------|----|----|----|--------|
| **Identified** | invitee's user id | invitation id | **true** | Answers attributable; counts as done; no reminder. In-progress draft is **persisted and resumable** via the link (§8). |
| **Completion-tracked** | `null` | `null` | **true (flag only — no time)** | "Mark me as done but don't attach my answers to me." Answers anonymous; participation recorded so they're **not chased** and counted in the response rate. **No completion time is stored** — a timestamp would correlate with this response's submit time and unmask them. Not resumable (no link) → restarts if reopened. |
| **Fully anonymous** | `null` | `null` | **false (untouched)** | No trace that *this* person answered. **Trade-off:** because completion isn't recorded, a fully-anonymous invitee **may still receive the one reminder** (disclosed on the choice step). Not resumable → restarts. |

> **Timing side-channel (why `Completed` is a bool, not a timestamp):** recording *when* a completion-tracked invitee finished would let an admin match that user-linked time against the unattributed response's `SubmittedAt` and re-identify them. So completion is stored as a boolean fact only; `SurveyInvitation` has no completion-time column (and no `UpdatedAt`), and individual response submissions are **not** audit-logged.

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

### 6.1 Assisted translation (Google Cloud Translation)

Authoring N cultures by hand is the friction point in §6. We offer a **"pre-fill translations"** action that machine-translates a source culture into the others, leaving the author to **review and edit** before publish — never an auto-publish of raw machine output.

- **Flow:** author writes the survey in one source culture → clicks *Translate to other languages* → every `LocalizedText` (title, intro, thank-you, prompts, help, option labels, scale labels) missing in a target culture is filled from the source via the API → fields land **editable**, flagged "machine-translated" until the author confirms. Re-running only fills blanks (never clobbers an author-edited string) unless the author explicitly chooses "retranslate".
- **Why human-in-the-loop:** machine translation into Catalan and idiomatic es/it is good-not-perfect for a member-facing nonprofit voice; pre-fill-then-edit matches the existing one-culture-at-a-time authoring without inventing a new flow.
- **Reuse, don't bolt on:** the codebase already authenticates to Google with a service account (`GoogleWorkspace:ServiceAccountKeyJson`, `GoogleCredentialLoader`, the `Google.Apis.*` packages). This adds **one more enabled API on the same GCP project** — not a new integration. New client follows the existing external-client pattern (MailerLite/Directory): `IGoogleTranslationClient` (Application interface) → `GoogleTranslationClient` (Infrastructure, reusing `GoogleCredentialLoader`) + a `StubGoogleTranslationClient` for dev/test. Survey calls it through a service interface (cross-section to GoogleIntegration), never imports `Google.Apis.*`.
- **Cost — negligible.** Billed per source character × target language. A 10–15-question multiple-choice survey is ~4,000 source chars → ~16,000 chars to fill 4 other cultures. At the standard NMT rate ($20 / 1M chars) that's **~$0.32**, but the **free tier is 500,000 chars/month** (resets monthly), so realistic spend is **$0** — you'd need ~30 full surveys/month to exhaust it. Standard NMT is sufficient for short choice prompts; no need for the pricier LLM/adaptive tiers.
- **Graceful absence:** if Google credentials aren't configured (dev/test, or the API isn't enabled), the stub is registered and the button is simply hidden — manual translation still works. Same `hasGoogleCredentials` gate the Workspace clients already use.

### 6.2 Response translation (free-text answers)

Free-text answers (`ShortText` / `LongText`) come back in whatever language the respondent answered in. The results view (§8) and the analysis API (§13) can **translate answers on read** into the reader's culture, reusing the **same `IGoogleTranslationService`** from §6.1 — no second mechanism.

- **On-demand, not stored:** translation happens at read time (results view "translate" toggle, or API `?translateTo={culture}`). We don't persist translated copies — the original answer in `SurveyAnswer.TextValue` stays canonical; translation is a display/transport convenience. (Optional cache keyed by `(answerId, targetCulture)` if volume ever warrants — not v1.)
- **Choice/rating answers** need no translation — they're keyed by the culture-neutral `Option.Value`, already labelled per culture from the authored `LocalizedText`.
- **Cost** is the same trivial NMT rate; a few hundred short answers is well inside the monthly free tier.
- **Egress note:** translating answers sends member-authored text to Google. Acceptable under the same posture as §6.1, but it's a third-party data flow over potentially sensitive free-text — see the consent/egress open question (§15).

## 7. Distribution & reminders (reuse Campaigns/Mailer + Email outbox)

**Audience resolution** reuses the Mailer **`IMailerAudience`** framework — the existing `ComputeMemberUserIdsAsync` implementations already cover *all-users*, *ticket-holders* (`HasTicketAudience`), team/shift cohorts, etc. The Survey author picks an `AudienceKey`; the send flow resolves it to a user-id set the same way audience sync does today.

> **Reuse boundary flagged for Peter (Open Q):** `IMailerAudience` lives in the Mailer section and is coupled to MailerLite group names. Cross-section reuse should go through a **read interface** (e.g. an `IAudienceResolver` / `IMailerServiceRead.ResolveAudienceAsync(key)`) rather than Survey importing Mailer internals — exact surface to be settled at implementation, consistent with the hard-rules cross-section pattern. Fallback if that proves heavy: Survey resolves cohorts directly via the same cross-section reads the audiences use (`ITicketServiceRead`, `ITeamService`, `IUserServiceRead`).

**Send model — idempotent per-recipient ledger (the load-bearing business rule).** A survey is not a one-shot blast; it accumulates **invitations**, one per person. Each "send" is a **top-up**: resolve a target set, **diff it against the existing invitations**, and create + email only the net-new recipients.

- Send #1 to {A, B} → invitations for A, B. Send #2 to {B, C, D} → B already has one (skipped) → only C, D are invited. End state: A, B, C, D each invited **exactly once**.
- Each invitation carries **its own** sent / completed / reminded clock, so a later top-up never resets earlier invitees' reminder timing.
- A send **only ever adds** — it never revokes. If the target set shrinks, already-invited people keep their invitation.
- You can keep topping up while the survey is Open.

**Send flow** (`SurveyService.SendInvitesAsync`, mirrors a Campaign wave):
1. Resolve the target set → user-id set (see the open targeting question, §15.2).
2. Diff against existing `SurveyInvitation`s for this survey; create one per **net-new** user (idempotent on `(SurveyId, UserId)`).
3. For each new invitation, build a tokenised link (`/Survey/Answer?t={token}` — see §7.1) and enqueue a `SurveyInvitation` email via `IEmailService`, localised to `PreferredLanguage`. Stamp `SentAt` + `LatestEmailStatus`.

### 7.1 Invite token — identify, don't authenticate, don't let randoms spoof

The link must let us **identify the recipient** (recover their user GUID server-side, so the response can be attributed when they choose *Identified*, and so completion/reminders track the right person) while satisfying two hard constraints:

- **Not a login.** The token grants exactly one capability — open *this* survey as *this* invitee — and never establishes a session, cookie, or any access to other pages. It is purpose-scoped (`"survey-invitation"`), like the unsubscribe token's `CommunicationPreferences` purpose. A leaked link exposes only that one survey context.
- **Unforgeable.** A random who copies someone's user GUID (or guesses one) **must not** be able to construct a valid link.

**Mechanism — keyed signature, not a reversible hash.** The token is an ASP.NET **Data Protection** payload (`IDataProtectionProvider.CreateProtector("survey-invitation").Protect(...)`) carrying the `InvitationId` (which maps 1:1 to `SurveyId` + user GUID). Data Protection encrypts **and MACs** the payload with a server-held key, so the token is effectively a signed, opaque encoding of the GUID that **cannot be produced without the server key**.

> **Why not rot13 / a plain hash of the GUID:** any reversible obfuscation (rot13, base64, XOR) or an *unkeyed* hash is trivially reproducible — anyone with a target's GUID can compute the same string and spoof the invite. The anti-spoof property comes specifically from the **secret key** in the MAC/encryption, not from the encoding being unreadable. So we use the keyed Data Protection token (the codebase's existing primitive for exactly this), not a homegrown obfuscation. Same reason we don't roll our own here: the magic-link and unsubscribe flows already depend on this primitive.

Optional hardening (cheap, recommended): give the token a **lifetime** tied to the survey's open window via `ITimeLimitedDataProtector`, so stale links stop resolving after `ClosesAt`.

**Reminder job** (`SendSurveyReminderJob : IRecurringJob`, daily cron):
- Selects invitations where `Survey.Status == Open`, `Completed == false`, `SentAt <= now - 7d`, `ReminderSentAt is null`.
- Enqueues one `SurveyReminder` email; stamps `ReminderSentAt`. One reminder per invitee, ever.

Both honour the existing outbox pause flag. Survey invites are **operational, never marketing** (sent as `MessageCategory.System`, always-send), so they are **not** gated by the marketing opt-out or any Mailer audience exclusion — they go directly to the targeted ticket-holders or members (§15.2).

## 8. Wizard (the answering flow)

`/Survey/Answer?t={token}` (invited) or `/Survey/{publicSlug}` (public) — both `[AllowAnonymous]`, like `WelcomeController`/`GuestController`.

1. **Step 0 — intro + privacy + language.** Renders `Survey.Intro`. If `AllowAnonymous`, shows the three-choice privacy selector (§4) with the reminder trade-off note. Anonymous/public path shows a language picker.
2. **Question pages.** One page at a time; required-visible questions validated server-side; branching evaluated on each advance. The first advance past the intro records the "started" funnel signal — `Invitation.Started = true` for the link path, or an increment to `Survey.PublicStartedCount` for the slug path (anonymous visitors have no per-person anchor); the entry path (`UserSpecificLink` / `Slug`) is recorded on the response as `InputMethod`.
3. **Submit.** Finalises the `SurveyResponse` (+ `SurveyAnswer`s) with the chosen `Anonymity` (sets `SubmittedAt`); flips `Invitation.Completed = true` for Identified/Completion-tracked (boolean only, no time). Renders `Survey.ThankYou`.

**Resume (in-progress only):** an **Identified** respondent who re-clicks their invite link mid-survey resumes their persisted draft (found by `(SurveyId, UserId, SubmittedAt is null)`) — within the open window, no login, via the Data-Protection token. **Completion-tracked / Anonymous** responses carry no user/invitation link, so there is nothing to find on return → they **restart** from the beginning. Editing an already-submitted response is out of v1 (resume is for unfinished drafts only).

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
| `IGoogleTranslationService` (GoogleIntegration, new) | Pre-fill authored `LocalizedText` translations (§6.1) |
| `IAuditLogService.LogAsync` | Survey lifecycle + send audit entries |
| `IDataProtectionProvider` | Tokenised invite/edit links (purpose `"survey-invitation"`) |
| `IUserDataContributor` (implemented by `SurveyService`) | GDPR export of a user's **Identified** responses (`GdprExportSections.Survey`, new) |

`Survey` sits **above** Users / Profiles / Tickets / Teams / Mailer / Email / GoogleIntegration — it calls into them, never the reverse.

## 11. GDPR

- **Export:** `SurveyService` implements `IUserDataContributor`; a user's export includes their `Identified` responses (survey title, submitted-at, their answers). `CompletionTracked` / `Anonymous` responses are unattributable and excluded.
- **Deletion / anonymisation:** on user deletion, `SurveyInvitation.UserId` and `Identified` `SurveyResponse.UserId` follow the section's standard FK-SetNull/anonymise path; the response content for already-anonymous tiers is untouched (no PII).
- **Consent framing:** survey invites are operational membership communication, not marketing; the privacy choice + clear intro copy is the lawful-basis surface for storing identified answers.

## 12. Architecture (per design-rules §15)

- **Owning service:** `SurveyService` (`Humans.Application.Services.Survey`). Application-layer; never imports `Microsoft.EntityFrameworkCore`. Calls its own repo + other sections' **service interfaces** only.
- **Repository:** `ISurveyRepository` (`Humans.Infrastructure/Repositories/Survey/SurveyRepository.cs`). Owns the SQL surface; `IDbContextFactory<HumansDbContext>` per call.
- **Owned tables:** `surveys`, `survey_questions`, `survey_question_options`, `survey_invitations`, `survey_responses`, `survey_answers`.
- **Job:** `SendSurveyReminderJob` in `Humans.Infrastructure/Jobs/`, resolves `SurveyService` via DI, goes through the service interface (no repo from the job).
- **No cross-domain navs and no cross-section FK constraints:** Users/Teams are referenced by bare `Guid` FK columns (the `FeedbackReport.AgentConversationId` precedent). The service stitches display data via cross-section reads into DTOs. **No `[Obsolete]` members** — a new section is not born with grandfathered debt.
- **Caching:** none in v1 (admin-authored, read-on-demand; results aggregation is a per-survey query at 500-user scale). Add a decorator only if a hot read emerges.
- **Status:** (A) born §15-compliant.
- **Architecture tests:** add `SurveyArchitectureTests` pinning no-EF-in-Application, single-repo table ownership, and cross-section-via-interface.

## 13. Results, export & external analysis (API)

The point of a survey is the readout. We have two known consumers, and they want different shapes:

- **App-feedback survey** ("how did you like the app, what would you change") → handled by **Claude reading the responses over an API**, turning free-text into a bug/improvement list. We are **not** building that analysis in-app; we are building **the API Claude reads**.
- **Event survey** → **raw export** (CSV/JSON), with infographics produced **externally** (Claude over the export). Again, no in-app charting engine.

So the section ships three things and stops there: an in-app results view (aggregates), a raw export, and a read-only analysis API. Synthesis (themes, bug extraction, infographics) lives **outside** this effort.

### 13.1 In-app results view (§8 admin)

Per-question aggregates (counts/%, rating distributions, free-text answer list — the §6.2 translate toggle is deferred), response-rate (responses ÷ invited), the **participation funnel** (started vs finished, split by input method — `UserSpecificLink` vs `Slug`), and an Identified-only per-respondent drill-down. This is the at-a-glance view — not the analysis surface.

### 13.2 Raw export

Admin download from `/Survey/Admin`:
- **CSV** — one row per response, one column per question (multi-choice flattened to `value|value`); an `anonymity` column; user identity columns populated **only** for `Identified` responses.
- **JSON** — the same data structured (response → answers), the format an LLM/agent ingests most cleanly. Optional `?translateFreeText={culture}`.

Anonymous and completion-tracked responses export their answers with **no** user-identifying columns, by construction (there is nothing to join).

### 13.3 Analysis API (key-authed, read-only) — the "Claude reads the responses" surface

Mirrors the existing **Issues API** pattern exactly (`X-Api-Key` header; **503** if the key env var is unset, **401** if invalid), so the tooling and the `/triage`-style skill story are consistent.

| Route | Purpose |
|-------|---------|
| `GET /api/surveys` | List surveys (id, title, status, response/invite counts). |
| `GET /api/surveys/{id}` | Survey definition — questions, options, types, branching — so the reader knows the shape. |
| `GET /api/surveys/{id}/responses` | All responses + answers. **`?format=md`** returns a token-lean **Markdown table** (one row per response — the compact shape for an agent reading the bulk); default is JSON. `?anonymity=`, `?since=`, `?limit=&cursor=` paging. (`?translateFreeText=` is part of the deferred translation slice, §6.2.) |
| `GET /api/surveys/{id}/aggregates` | Pre-computed per-question aggregates **plus the participation funnel** — started vs finished counts split by input method (`UserSpecificLink` vs `Slug`) — the cheap path when the consumer only needs counts. |

- **Read-only.** No write routes. The API never creates issues, never mutates a survey — extraction of bugs/work items from the app-feedback survey is a **human+Claude step done elsewhere**, deliberately out of this section.
- **Identity exposure follows anonymity tier**: `Identified` responses include the respondent's user id + display name; `CompletionTracked`/`Anonymous` responses expose answers only. Enforced server-side regardless of query params.
- **Env var:** `SURVEY_API_KEY` (+ an `/Admin/Configuration` set/unset indicator, mirroring `ISSUES_API_KEY`).
- **Stable shape:** enums serialised as strings; question/option `Value`s are the culture-neutral join keys so an agent can correlate answers across languages without translating choice labels.

### 13.4 What this deliberately does NOT include

In-app theme clustering, sentiment, summarisation, auto-generated infographics/charts, or auto-filing bugs from feedback. Those are produced by Claude over §13.2/§13.3 and are **out of scope** (see §14). The section's contract is: clean structured data out, by a stable read API + export. That keeps member free-text from being run through an in-app LLM pipeline nobody asked us to own, and keeps the egress decision (§15) explicit and one-hop.

## 14. Out of scope (v1)

**Deferred to a fast-follow (decided 2026-06-04 — planned, not abandoned):**

- **Google translation client (whole slice).** The `IGoogleTranslationClient`/`IGoogleTranslationService` in GoogleIntegration is deferred, which defers **both** §6.1 authoring "pre-fill translations" **and** §6.2 free-text answer translate-on-read (they share that one client). v1 authoring is **manual per-culture** only; v1 results/API serve free-text **as-submitted** (no translate toggle, no `?translateFreeText`). All Google-translation features land together as one later slice. **Consequence:** v1 has **no Google data egress** — the only external data flow in v1 is the §13.3 analysis API/export to Claude.
**Permanently out of scope:**

- **In-app analysis/synthesis** — theme clustering, sentiment, summarisation, infographic/chart generation, and auto-extracting bugs/work-items from feedback. Done externally by Claude over the §13 API/export; the app ships data, not analysis.
- Response **quotas / caps**, randomised question order, A/B variants.
- Piped text / answer interpolation into later prompts.
- File-upload answer type.
- Multiple reminders / configurable reminder cadence (v1 = exactly one, at 7 days).
- SMS / push delivery — email only.
- Realtime results dashboards / charts beyond basic aggregates + CSV/JSON export.
- A `SurveyAdmin` delegation role.
- Cross-survey respondent identity / longitudinal linking.
- **Persisted/stored translations** of either authored content or answers — translation is on-read (§6.1/§6.2).

## 15. Decisions (resolved 2026-06-04)

The §15 open questions are now settled. Each resolution below is binding for v1.

1. **`LocalizedText` scope** → **Survey-owned** value object. Reuse-first/YAGNI: don't build a shared Domain primitive before a second consumer (Events/Camps) exists; promotion is a clean later refactor. (§6)
2. **Audience.** **Locked (business):** the send model is an **idempotent per-recipient invitation ledger** — top-up sends diff against existing invitations; nobody is double-invited; sends never revoke (§7). **Predicates (decided 2026-06-04):** v1 ships **Team** first (a team's members), then the easy cohorts — **all active members**, **ticket-holders**, **shift participants**. (These mirror cohorts the Mailer audiences already express.) **Marketing opt-out (decided 2026-06-04):** surveys are **never marketing** — recipients are ticket-holders or members, and an invite is operational comms — so invites **do not** honour the marketing opt-out. They are sent as `MessageCategory.System` (always-send), bypassing the marketing exclusion entirely. **Deferred (tech impl):** where predicates are computed / whether a cross-section read interface is introduced — not decided now.
3. **Fully-anonymous reminder trade-off** → **accept** the documented behaviour (a fully-anon invitee may receive the one reminder). The alternative — a privacy-leaking "answered" bit — is rejected; never-leak wins. Disclosed to the respondent on the choice step. (§4)
4. **Public slug** → **public-link answering path is IN v1** (decided 2026-06-04). A survey with a `PublicSlug` set (requires `AllowAnonymous`) can be answered at `/Survey/{slug}` with no invitation; such responses are always **Anonymous**, `InputMethod = Slug`. Public starts are counted by a per-survey integer counter (`Survey.PublicStartedCount`) — anonymous visitors have no per-person anchor; finishes are the submitted `Slug` responses. (§4, §8)
5. **Branch sources** → **choice-question predicates only** in v1; rating/text branch sources are not built. (§5)
6. **Assisted translation** → **deferred** (§14). v1 ships **manual per-culture** authoring. The Google translation client and both §6.1 pre-fill and §6.2 answer-translate features land together later.
7. **Data egress / consent** → with §6/§6.2 translation deferred, v1 has **no Google egress**; the only external data flow is the §13.3 analysis API/export to Claude. Posture: (a) a short **transparency note on the wizard intro** — responses may be reviewed/analysed, including by automated tooling; (b) the existing **server-side anonymity-tier gating** on the API/export (identified responses expose identity; completion-tracked/anonymous never do). **No** per-survey "anonymise the payload" toggle in v1 (YAGNI — a survey needing that simply doesn't collect identified responses).
8. **Analysis API scope** → **in v1**. It is the stated point (Claude reads app-feedback responses), it's small, and it mirrors the Issues API. (§13.3)

## 16. Acceptance criteria

- An `AdminOrBoard` user can author a multi-question survey with single/multi-choice, text, and rating questions, translate it into ≥2 cultures, and add a branching rule that hides a question based on a prior choice answer.
- _(Fast-follow — deferred per §14/§15.6)_ The "pre-fill translations" action machine-translates missing `LocalizedText` from the source culture into the others, lands them editable/flagged, never overwrites author-edited strings, and is hidden when Google credentials are absent (stub registered).
- Publishing with an `AudienceKey` creates one `SurveyInvitation` per resolved recipient and enqueues a localised invite email per recipient through the outbox.
- A recipient opens the tokenised link without logging in; when `AllowAnonymous`, the first step offers Identified / Completion-tracked / Fully-anonymous and (for public/anon) a language picker.
- Branching: a question whose `ShowIf` is unmet is skipped and, even if required, does not block submission; the server rejects answers to questions that should have been hidden.
- An identified submission sets `Response.UserId`/`InvitationId` and flips `Invitation.Completed = true`; a completion-tracked submission flips `Completed = true` with **no user link and no timestamp**; a fully-anonymous submission sets neither a user link nor `Completed`.
- Completion is recorded as a boolean — no completion-time is persisted anywhere, so a completion-tracked response cannot be re-identified by matching its submit time against a user's completion time.
- An Identified respondent who re-opens their invite link mid-survey resumes their draft at the first unanswered page; completion-tracked/anonymous respondents start over.
- Seven days after send, un-completed invitations receive exactly one reminder email; completed (Identified/Completion-tracked) invitees receive none.
- Results view shows per-question aggregates and a per-respondent drill-down limited to Identified responses; CSV **and JSON** export work (free-text served as-submitted).
- _(Fast-follow — deferred per §14/§15.6)_ The results view (and API) can translate free-text answers into a chosen culture on read, without persisting the translation.
- `GET /api/surveys/{id}/responses` with a valid `SURVEY_API_KEY` returns responses + answers (paged); identity fields appear only for Identified responses; the survey definition endpoint exposes question/option `Value`s as stable join keys. Missing/invalid key → 503/401.
- No in-app code clusters, summarises, charts, or auto-files anything from responses — synthesis is external over the API/export.
- A user's GDPR export includes their Identified responses and omits anonymous/completion-tracked ones.
- Architecture tests pin: no EF in `Humans.Application.Survey`, `survey_*` tables owned solely by `SurveyRepository`, all cross-section access via service interfaces.
