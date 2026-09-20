# Surveys — target shape

Derived fresh each section-doctor run, before any scan. The section's invariants live in
[`Surveys.md`](Surveys.md); this file is what the section *should* look like.

## 1. What the section does

Someone writes a questionnaire in as many languages as they care to, decides who should answer it,
and hands it to the Board to send. The Board reads it, and either sends it — each of those people
gets their own link — or hands it back with a reason. People answer it, from that link or from a
public address the survey can be given, and choose how much of themselves the answer carries:
their name, only the fact that they took part, or nothing at all. The Board then reads what came
back, in the app or as a downloaded file, and sees who has not answered yet so a single reminder
can go out. A person's own answers, and the surveys they wrote, follow them out of the system when
they ask to be forgotten; the questionnaires and the answers stay, the person does not.

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
| **Approve** | "May this go out, and who said so?" | submit-for-approval, the Board queue, approve-and-send, reject with a reason |
| **Lifecycle** | "Is this survey taking answers?" | Open, Close, the open/close window, the official link |
| **Reach** | "Who gets asked, has it gone out, who is still silent?" | audience resolution, send, per-invite status, the daily reminder sweep |
| **Answer** | "How do I fill this in, and how much of me does it carry?" | invited wizard, public wizard, anonymity choice, draft resume |
| **Decide** | "Who may vote, once, unseen until it closes — and what did they decide?" | Asociado-vote gate at entry/answer/submit, definition lock, embargo, ranked count, post-close availability recount |
| **Read** | "What came back?" | scoped results, ballot drill-down, CSV, JSON, the analysis API |
| **Forget** | "What of mine is here, and can it leave?" | GDPR export contribution, Article 17 erasure, account-merge reassignment |

The section's weight sits in **Answer**, **Read** and **Decide** — one wizard serving two entry
paths, one response set projected into results, CSV, JSON and the analysis API, and one
eligibility-and-embargo contract layered over both. **Approve** is the newest shape and the
thinnest: one status, one queue, two verbs.

## 3. Structure

Written fresh, not as today's layout with fixes.

- **One service.** Authoring, approval, sending, answering, results, voting and GDPR are one lane
  over one aggregate; splitting them would put the anonymity contract — and now the embargo and
  the approval gate — in more than one place. The service is the only repository caller and
  returns DTOs.
- **One repository over all of `survey_*`.** Those tables are touched nowhere else, and
  `ISurveyRepository` is internal so that cannot be arranged by accident.
- **Controllers split by audience, not by verb** — `SurveyAdmin` (anyone who may author, plus the
  Board) and `Survey` (respondent), and no others. Preview is authoring's rehearsal and approval
  is authoring's gate, so both live on the admin controller; preview renders the respondent views
  rather than copies of them.
- **Authoring is open, sending is not.** The admin controller's class policy is app access, and
  the Board-only verbs carry their own policy; the index is scoped to what the viewer authored.
  The service, not the view, decides who sees which surveys.
- **A resource handler holds the per-survey rules, and the service holds them again.** The handler
  is the browser's copy — it decides whether to render a button; the service is the enforcing copy
  and refuses regardless. Neither is redundant: one is a view concern, one is the invariant. Submit
  checks the author, Approve and Reject check Board/Admin, the admin list scopes itself, and every
  edit path — `UpdateCoreAsync`, translation pre-fill, ranked availability — takes a `SurveyViewer`
  and compares it to `CreatedByUserId`, so an edit the handler refuses the service refuses too.
- **One page flow.** Both entry paths differ only in how the session is keyed and where the
  redirects land; that difference is one small route record, and everything else is shared.
- **Pure helpers hold the rules that can be decided without the database**: branch visibility,
  page ordering, answerability, grid normalisation, and the ranked-choice count (pairwise,
  Ranked Pairs, Condorcet, Borda). They are what tests reach for first.
- **A vote is a survey with a flag, not a second aggregate.** Eligibility, embargo, lock and
  no-reopen are branches inside the existing flows, keyed off that flag; there is no parallel
  ballot table or vote controller.
- **The public surface is `ISurveyAnalysisRead` for the machine API, `ISurveyReminderSender` for
  the job, and the two GDPR contributor interfaces, and nothing else.** Everything else is
  internal to the section.
- **Contracts carry data, not behaviour.** The enums and read models are public because they
  cross the boundary; the editing shape does not cross it and stays internal.

## 4. Invariants

Stated in full in [`Surveys.md`](Surveys.md). The ones the structure exists to protect, each with
the line that enforces it.

- **Identity is written onto a response only for the Identified tier**; the other two tiers leave
  no link, and nothing downstream — results, export, API, drill-down — can re-attach one:
  `src/Sections/Humans.Surveys/Services/SurveyService.cs:1188` writes it, `:1209` and `:1237`
  refuse to, and the read paths honour the tier at `:1640` and `:1685`.
- **No completion timestamp for a tracked-but-unlinked answer, and no `UpdatedAt` on the ledger**
  — a time is a join key: `src/Sections/Humans.Surveys/Domain/SurveyInvitation.cs:15`, with the
  shared epoch that keeps a public start uncorrelated at
  `src/Sections/Humans.Surveys/Services/SurveyService.cs:1015`.
- **Branching is decided on the server at submit**; a hidden question's answer never lands:
  `src/Sections/Humans.Surveys/Services/SurveyService.cs:1150`, over the effective-state pass at
  `:2193`.
- **Sending is additive and idempotent** — the same audience resolved twice invites nobody twice,
  and never revokes: `src/Sections/Humans.Surveys/Services/SurveyService.cs:716`, with the
  re-queue-not-re-create branch at `:739`.
- **Exactly one reminder per invitee, anchored on `ReminderSentAt`**:
  `src/Sections/Humans.Surveys/Data/SurveyRepository.cs:238` selects only the unstamped,
  `:250` stamps them.
- **Individual submissions are never audit-logged; survey lifecycle, sends and availability
  recounts always are**: the lifecycle writes sit at
  `src/Sections/Humans.Surveys/Services/SurveyService.cs:544`, `:559`, `:787` and `:881`, and the
  submit path at `:1179`–`:1236` has no `auditLog` call at all.
- **Preview writes nothing to this section's tables** — no invitation, response, draft, reminder
  or funnel event. The rendering previews build view models and call no writing service method:
  `src/Sections/Humans.Surveys/Controllers/SurveyAdminController.cs:242` through
  `src/Sections/Humans.Surveys/Models/SurveyPageViewModelFactory.cs:27`. The one preview action
  that writes anywhere is send-preview-to-self, which queues a single email to the requester
  through the Email crosscut and still creates no Surveys row
  (`src/Sections/Humans.Surveys/Services/SurveyPreviewEmailService.cs:44`).
- **A submitted survey leaves PendingApproval only through approval or rejection.** A Draft is
  submitted by its author and by nobody else
  (`src/Sections/Humans.Surveys/Services/SurveyService.cs:609`); once pending it can be reached by
  neither of the two status verbs that would walk it back out — Open refuses it (`:533`) and Close
  refuses it (`:556`), so the `PendingApproval → Closed → Open` route to an unapproved Open survey
  does not exist. Board/Admin keep the pre-existing direct Draft → Open path, which the gate does
  not touch.
- **Approval cannot open a survey that has nobody to invite.** Approve-and-send checks the audience
  *configuration* and then resolves it to real recipients, both before the approval is persisted, so
  a Team audience whose team has been deleted fails while the survey is still in the queue where the
  Board can reject it back to its author. It records who approved and sends in the same step:
  `src/Sections/Humans.Surveys/Services/SurveyService.cs:632` checks the configuration, `:638`
  resolves it to recipients, and `:644` persists the approval.
- **A rejection carries a reason.** An empty note is refused, the note is trimmed and bounded, and
  the rejection is audited against the author:
  `src/Sections/Humans.Surveys/Services/SurveyService.cs:660`, `:668`–`:670` and `:674`.
- **An author sees only what they wrote; the Board sees everything.** The scoping is in the
  service, not the view: `src/Sections/Humans.Surveys/Services/SurveyService.cs:565`, with the
  browser-side copy of the same state machine at
  `src/Sections/Humans.Surveys/Authorization/SurveyAuthorizationHandler.cs:30`.
- **An Asociado vote is CompletionTracked only, targets the Asociados audience only, and checks
  eligibility at entry, on every page, and again at submit** — current status, not status at send:
  `src/Sections/Humans.Surveys/Services/SurveyService.cs:971`, `:1269` and `:1131`.
- **Eligibility requires the stored id to be the live one.** A merged-away id resolves to an
  eligible survivor, and answering under it would give one Asociado two ballots:
  `src/Sections/Humans.Surveys/Services/SurveyService.cs:957`.
- **While an Asociado vote is Open, nothing answer-derived leaves the service**: results, both
  exports, the analysis API and the drill-down all return participation only:
  `src/Sections/Humans.Surveys/Services/SurveyService.cs:1474`, `:1651` and `:1685`.
- **After close, a ballot is shown without name, id, participation id or timestamp, and exported
  without name or user id**, including any legacy Identified row:
  `src/Sections/Humans.Surveys/Services/SurveyService.cs:1502` and `:1506`.
- **An Asociado vote's definition and audience are frozen once it opens; a ranked question's
  counting settings freeze at the first saved answer of any survey**; the only post-close mutable
  input is ranked-option availability, which never rewrites a stored ballot:
  `src/Sections/Humans.Surveys/Services/SurveyService.cs:255` and `:426` for the vote,
  `:290` over `:2787` for the ranked settings.
- **A closed Asociado vote does not reopen**:
  `src/Sections/Humans.Surveys/Services/SurveyService.cs:539`.
- **The official ranked method is Ranked Pairs**; authored option order is the disclosed final
  tie-break; every other method is sensitivity analysis and never the headline:
  `src/Sections/Humans.Surveys/Domain/RankedQuestionSettings.cs:14` is what the builder writes,
  `src/Sections/Humans.Surveys/Services/SurveyService.cs:1574` is what the results page calls
  official.
- **A person leaving takes their answers and their authorship with them, and neither takes the
  survey with it.** Erasure anonymises responses and blanks authorship;
  an account merge moves authorship to the survivor:
  `src/Sections/Humans.Surveys/Services/SurveyService.cs:1872`, `:1873` and `:1882`, over
  `src/Sections/Humans.Surveys/Data/SurveyRepository.cs:488` and `:504`.
- **Three slices are exported**: the person's own Identified responses, the surveys they authored,
  and their invitation ledger (`src/Sections/Humans.Surveys/Services/SurveyService.cs:1841`, `:1842`
  and `:1843`). The ledger is what a member who was only invited, or who answered under completion
  tracking, gets — their response carries no `UserId`, so without it they would have nothing
  exported at all. It carries invitation-side timestamps only, which say nothing about when an
  anonymous answer arrived. Erasure deletes those rows outright:
  `src/Sections/Humans.Surveys/Data/SurveyRepository.cs:467`.

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
- **No notification to the author on approve or reject.** The decision is audited and visible on
  the author's own index; nothing is pushed to them.

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
- **No approval workflow entity.** The gate is one status plus two columns on the survey itself —
  no approval table, no step list, no delegation. A second approver, or a reason code, would be a
  different feature.
- **No re-submission state.** A rejected survey goes back to Draft and is submitted again through
  the same verb; there is no "revised" status distinguishing the second attempt from the first.

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
- **Ranked counting keeps the all-authored-options result beside the currently-available
  one** — because striking an option can break a preference cycle and change the winner even
  when the old winner is still available. Both are shown so the change is visible.
- **`CreatedByUserId` is `Guid.Empty` for nobody, and is written through the EF change tracker.**
  The property is init-only and non-nullable, so erasure and merge set it via
  `ctx.Entry(...).Property(...).CurrentValue` rather than an assignment. An unattributed survey
  carries the same value, so "erased author" and "never had one" are deliberately the same state.
- **`ApproveAndSendAsync` validates the audience *configuration* before it persists the approval,**
  and then sends with `CancellationToken.None`. A misconfigured audience fails while the survey is
  still pending, where the Board can reject it back to the author; a half-sent batch is worse than
  a slow request. Configuration is all that is checked — recipients are resolved later, inside
  `SendInvitesAsync`, so an audience that is well-formed but resolves to nobody still approves and
  opens.

## History

No score in this table — a number written here is stale the moment the run's PR takes another
commit, and the skill forbids the correcting commit that would chase it. The run's measurement,
stamped with the reforge version that produced it, lives in that run's file and in its PR.

| Date | Outcome | PR |
|---|---|---|
| 2026-08-27 | first doctor run — thank-you copy restored on the invited path, reminder window honoured, anonymous 500 closed, an all-hidden page no longer reports itself as completed, stale prose swept | peterdrier/Humans#1538 |
| 2026-09-07 | second run, after ranked-choice voting shipped — target gains the Decide shape; JSON export carries ranked ballots again; the ranked-freeze test exercises the freeze; Send page labels the Asociados audience; section docs caught up to the vote schema; comment cuts | peterdrier/Humans#1618 |
| 2026-09-20 | third run, after the §11 self-service authoring lane shipped — target gains the Approve shape and an enforcement cite on every invariant; docs, comments and two debt rows caught up to open authoring; reject modals left the table; `SurveyDetail` can no longer be built without an author; a dead resx key deleted in six cultures. Author-side navigation and preview stay Board-gated — raised for Peter, not struck | peterdrier/Humans#1747 |
