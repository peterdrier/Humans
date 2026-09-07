# Surveys — target shape

Derived fresh each section-doctor run, before any scan. The section's invariants live in
[`Surveys.md`](Surveys.md); this file is what the section *should* look like.

## 1. What the section does

Someone on the Board writes a questionnaire in as many languages as they care to, decides who
should answer it, and sends each of those people their own link. People answer it — either from
that link, or from a public address the survey can be given — and choose how much of themselves
the answer carries: their name, only the fact that they took part, or nothing at all. The Board
then reads what came back, in the app or as a downloaded file, and sees who has not answered yet
so a single reminder can go out. A person's own answers follow them out of the system when they
ask to be forgotten; the answers stay, the person does not.

The same machinery also runs a binding vote of the association's voting members: only current
Asociados may cast a ballot, each casts exactly one, nobody — not even the Board — sees a single
answer until the vote closes, and a closed vote can never be reopened. Ranked-choice questions
let voters order options, tie them, or reject them outright; the count is precommitted to one
method, and options that later prove unavailable can be struck from the count without touching a
single stored ballot.

## 2. The shapes

These question-shapes cover every route, contract method and job in the section.

| Shape | The question it answers | Where it is asked |
|---|---|---|
| **Author** | "What am I asking, and in which languages?" | builder GET/POST, machine translation pre-fill |
| **Rehearse** | "What will this look like before anyone sees it?" | preview intro/page/thank-you, email preview, preview-email-to-self |
| **Lifecycle** | "Is this survey taking answers?" | Open, Close, the open/close window, the official link |
| **Reach** | "Who gets asked, has it gone out, who is still silent?" | audience resolution, send, per-invite status, the daily reminder sweep |
| **Answer** | "How do I fill this in, and how much of me does it carry?" | invited wizard, public wizard, anonymity choice, draft resume |
| **Decide** | "Who may vote, once, unseen until it closes — and what did they decide?" | Asociado-vote gate at entry/answer/submit, definition lock, embargo, ranked count, post-close availability recount |
| **Read** | "What came back?" | scoped results, ballot drill-down, CSV, JSON, the analysis API |
| **Forget** | "What of mine is here, and can it leave?" | GDPR export contribution, Article 17 erasure |

The section's weight sits in **Answer**, **Read** and now **Decide** — one wizard serving two
entry paths, one response set projected into results, CSV, JSON and the analysis API, and one
eligibility-and-embargo contract layered over both.

## 3. Structure

Written fresh, not as today's layout with fixes.

- **One service.** Authoring, sending, answering, results, voting and GDPR are one lane over one
  aggregate; splitting them would put the anonymity contract — and now the embargo — in two
  places. The service is the only repository caller and returns DTOs.
- **One repository over all of `survey_*`.** Those tables are touched nowhere else, and
  `ISurveyRepository` is internal so that cannot be arranged by accident.
- **Controllers split by audience, not by verb** — `SurveyAdmin` (Board) and `Survey`
  (respondent), and no others. Preview is authoring's rehearsal, so it lives on the admin
  controller and renders the respondent views rather than copies of them.
- **One page flow.** Both entry paths differ only in how the session is keyed and where the
  redirects land; that difference is one small route record, and everything else is shared.
- **Pure helpers hold the rules that can be decided without the database**: branch visibility,
  page ordering, answerability, grid normalisation, and the ranked-choice count (pairwise,
  Ranked Pairs, Condorcet, Borda). They are what tests reach for first.
- **A vote is a survey with a flag, not a second aggregate.** Eligibility, embargo, lock and
  no-reopen are branches inside the existing flows, keyed off that flag; there is no parallel
  ballot table or vote controller.
- **The public surface is `ISurveyAnalysisRead` for the machine API and `ISurveyReminderSender`
  for the job, and nothing else.** Everything else is internal to the section.
- **Contracts carry data, not behaviour.** The enums and read models are public because they
  cross the boundary; the editing shape does not cross it and stays internal.

## 4. Invariants

Stated in full in [`Surveys.md`](Surveys.md). The ones the structure exists to protect:

- Identity is written onto a response only for the Identified tier; the other two tiers leave no
  link, and nothing downstream — results, export, API, drill-down — can re-attach one.
- No completion timestamp for a tracked-but-unlinked answer, and no `UpdatedAt` on the ledger:
  a time is a join key.
- Branching is decided on the server at submit; a hidden question's answer never lands.
- Sending is additive and idempotent — the same audience resolved twice invites nobody twice,
  and never revokes.
- Exactly one reminder per invitee, anchored on `ReminderSentAt`.
- Individual submissions are never audit-logged; survey lifecycle, sends and availability
  recounts always are.
- Preview creates nothing — no invitation, response, draft, reminder or funnel event.
- An Asociado vote is CompletionTracked only, targets the Asociados audience only, and checks
  eligibility at entry, on every page, and again at submit — current status, not status at send.
- While an Asociado vote is Open, nothing answer-derived leaves the service: results, both
  exports, the analysis API and the drill-down all return participation only.
- After close, a ballot is shown and exported without name, id, participation id or timestamp,
  including any legacy Identified row.
- An Asociado vote's definition and audience are frozen once it opens; a ranked question's
  counting settings freeze at the first saved answer of any survey; the only post-close mutable
  input is ranked-option availability, which never rewrites a stored ballot.
- A closed Asociado vote does not reopen.
- The official ranked method is Ranked Pairs; authored option order is the disclosed final
  tie-break; every other method is sensitivity analysis and never the headline.

## 5. Seams

Specified but not built. Not this run's work; noted because items touching these are shaped by
them.

- **Reminder customisation.** Invitation subject/message are author-editable; the reminder's
  wording is not, and the feature doc says so deliberately.
- **Results filtering by question.** The scope selector splits by anonymity tier only; there is
  no cross-tab.
- **Ranked data over the analysis API.** The definition snapshot and export rows already carry
  ranked settings and ballots; Backdoor's controller does not project them yet. That is
  Backdoor's lane to finish.
- **IRV, Baldwin and Coombs** are named in the feature doc as deferred sensitivity methods.

## 6. Deliberately not done

- **No caching decorator.** Admin-authored, low-traffic, per-invitee writes. A cache here would
  buy nothing and put a stale question graph in front of a live respondent.
- **No cross-section navigation properties.** Users, teams, tickets and shifts are bare `Guid`
  columns; display data is stitched in by the service through read interfaces. This is what keeps
  the schema from coupling to four other sections.
- **No `ISurveyServiceRead`.** It shipped empty and was deleted; the machine API's needs are a
  different shape and are served by `ISurveyAnalysisRead`.
- **No "other — please specify" option flag.** Authored as a gated `ShortText` question instead,
  which keeps `SurveyQuestionOption` at three fields.
- **No absence tests.** Cross-section repository injection does not compile; a test asserting it
  cannot happen would assert nothing.
- **No separate voting system.** The formal bylaw/quorum vote (nobodies-collective/Humans#86) is
  a different product; the Asociado vote here is a survey with a stricter contract, and stays one.
- **No authorable official method.** `RankedVotingMethod` is stored per question but the builder
  always writes Ranked Pairs; the precommitment is the feature, not a missing dropdown.

## Load-bearing weirdness

Settled decisions that look wrong until you know why. Do not re-litigate these.

- **`AuditEntityTypes` are literals, never `nameof`.** They are persisted strings matched by
  equality against rows already in the database; deriving them from a type name makes a rename
  silently empty the audit panel with no build error.
- **A CompletionTracked public start stamps a shared epoch `CreatedAt`,** not the real time. The
  real time would correlate with the unlinked response's `SubmittedAt`.
- **`SurveyWizardState.Answers` is keyed by `QuestionId.ToString()`.** Guid object keys do not
  round-trip through the session's JSON.
- **`Invitation.Completed` is a submit guard living inside a *resolve* method.** Submitting flips
  it, and `ResolveAnswerContextAsync` then returns null for that invitation — so every consumer
  downstream of a spent token inherits a gate written for one caller. Every behaviour defect found
  on 2026-08-27 was this one shape wearing different hats. Do not re-derive it; if a run
  wants to move the guard out of the resolver, that is a deliberate refactor, not a doctor strike.
- **A double-submit on an already-completed invitation lands on thank-you, not a 500** — the
  wizard path treats "already completed" as a normal submitted outcome. The standalone submit
  entry point still throws, and that asymmetry is intentional.
- **Grid, Information and RankedChoice questions may be branch targets but never branch
  sources.** None of them yields the flat option set a `ShowIf` clause matches on.
- **A ledger row with `SentAt = null` is participation, not an invitation** — it is excluded from
  invited counts, status and reminders until a real send stamps it.
- **Eligibility is re-checked at submit, not trusted from entry.** A member demoted mid-vote is
  refused at the last step; the doubled check is the point, not redundancy.
- **Ballots are ordered by response id in the drill-down**, not by submission time — a stable,
  meaningless order that cannot be aligned with the participation ledger.
- **Ranked counting keeps two results side by side** — all authored options and currently
  available options — because striking an option can break a preference cycle and change the
  winner even when the old winner is still available. Both are shown so the change is visible.

## History

No score in this table — a number written here is stale the moment the run's PR takes another
commit, and the skill forbids the correcting commit that would chase it. The run's measurement,
stamped with the reforge version that produced it, lives in that run's file and in its PR.

| Date | Outcome | PR |
|---|---|---|
| 2026-08-27 | first doctor run — thank-you copy restored on the invited path, reminder window honoured, anonymous 500 closed, an all-hidden page no longer reports itself as completed, stale prose swept | peterdrier/Humans#1538 |
