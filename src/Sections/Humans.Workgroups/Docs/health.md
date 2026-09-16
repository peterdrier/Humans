<!-- freshness:triggers
  src/Sections/Humans.Workgroups/**
  tests/Humans.Workgroups.Tests/**
-->

# Workgroups — Health

Last assessed: 2026-09-16 (section-doctor).

## 1. What the section does

Keeps the book of working groups the association has recognised, and the record of what each
one did.

Somebody with an idea fills in a short form: what the group is for, what it will hand over, to
whom, and by when. The Secretary looks at it and either recognises it or does not — nothing here
decides that by itself. Once recognised, the group gets a page, a shared folder, and a place to
write down what it is doing: minutes of its meetings, notes and updates in a running log,
drafts of whatever it is producing. The page, the roster, the log and the meetings are open to
anyone with access to the app, and anyone may join; a draft is the group's own until it publishes
it, so only its members and the Board see one. Once something is published, anyone may say
something about it while its window for remarks is open. When the group is finished — or has been
quiet long enough that the Secretary closes it — its page stays readable for good.

Nothing on this page acts on its own. Once a day the system looks for groups that have gone
quiet and applications nobody has answered, and it tells the people responsible. That is the
whole of its initiative: it writes notes, raises flags, and waits for a human.

## 2. The shapes

Everything the section exposes answers one of these questions.

| Shape | The question | Surfaces |
|---|---|---|
| **The register** | What groups exist, what state are they in, and what do they owe? | `GET /Workgroups`, `GET /Workgroups/{slug}`, the Governance card, the member dashboard card, the admin tile, the things-to-do entries |
| **Recognise it** | Should this group exist, and does it still? | `GET/POST /Workgroups/Apply`, `POST /Workgroups/Admin/{id}/Register`, `/Refer`, `/Refuse`, `/Withdraw`, `/Close`, `/Reactivate`, `POST /Workgroups/Admin/RegisterExisting` |
| **Who is in it** | Who is working on this, and who speaks for it? | `POST /Workgroups/{slug}/Join`, `/Leave`, `/Coordinators`, `POST /Workgroups/Admin/{id}/Coordinators` |
| **What did it do** | What happened, and when? | `POST /Workgroups/{slug}/Log/Save`, `/Log/{id}/Delete`, `/Meetings/Save`, `/Meetings/{id}/Delete`, `/Surveys/Link` |
| **What did it produce** | What is the group handing over, and what did people say about it? | `GET /Workgroups/{slug}/Documents/{id}`, `POST /Documents/Save`, `/Publish`, `/OpenComments`, `/CloseComments`, `/Deliver`, `/Comments`, `/Comments/RespondCategory`, `POST /Workgroups/{slug}/Comments/{id}/Respond`, `/Hide` |
| **The Board's reply** | What did the Board decide about what it received? | `POST /Workgroups/Admin/Documents/{id}/Disposition` |
| **Nudge the humans** | Who is behind, and who needs telling? | `RunDailyRhythmAsync` through `WorkgroupRhythmJob`, plus the read-time badges in `WorkgroupRhythm` |
| **Where do the files live** | Which Drive folder is this group's, and who may open it? | `GET/POST /Workgroups/Admin/Settings`, `WorkgroupDriveAccessSource`, `WorkgroupCalendarContributor` |
| **What do we hold on this person** | Export, erasure, account merge. | `ContributeForUserAsync`, `EraseForUserAsync`, `ReassignAsync` |

"Is this group allowed to do X?" is not one of the shapes. It collapses into one question the
service asks itself — is the group Active — and one the controller asks the authorization
handler.

## 3. Structure

A register with a rhythm on top. Written fresh, it is:

- **One table family**, one row per group with five child collections hanging off it, reached
  through a single repository that hands back the entire register as one detached graph.
  Nothing pages, nothing projects server-side; the dataset is a few dozen rows.
- **One service**, split into partials along the shapes rather than along the layers: the
  register's own rules, the Secretary's decisions, the documents and their comment period, the
  daily pass, the GDPR fan-outs, and a helpers file holding the projection, the guards and the
  one call site of each crosscut.
- **A pure-function rhythm module** over the projected record — no clock of its own, no service
  dependency — so the daily job and the page badges answer the same questions from the same
  code and can never disagree.
- **Two controllers**, one per audience: the member-facing group page, and the Secretary's
  queue. Each parses, authorizes, calls, and maps a rule failure to a message.
- **One caching decorator** holding the whole register under one key, cleared on every write,
  with nothing time-derived inside it.
- **Two inbound contributors** — the calendar feed and the Drive access source — that other
  sections call without naming Workgroups.

The layout matches, with one exception: the member controller's two dispatch helpers,
`ActAsync` and `FormAsync` (`Controllers/WorkgroupsController.cs:452`,
`Controllers/WorkgroupsController.cs:494`), carry the same three `catch` clauses over two
different result shapes — a redirect for a button, a re-render for a form. Two shapes, one
error map.

## 4. Invariants

- **Member work happens only on an Active group.** Every member-facing write calls
  `RequireAcceptsMemberWork` first, which throws unless the status is Active
  (`Services/WorkgroupService.Helpers.cs:102`). A member-facing write that does not call it is
  a violation; so is a Board action that does.
- **One or two coordinators, never zero by accident.** Setting coordinators rejects a count
  outside one-to-two (`Services/WorkgroupService.cs:269`) and rejects any id that is not a
  current member (`Services/WorkgroupService.cs:274`); the last coordinator leaving must name a
  replacement unless the caller is acting as admin (`Services/WorkgroupService.cs:175`).
- **The Secretary's refusals are written down.** Refuse and Withdraw both call `RequireReasons`
  before anything else (`Services/WorkgroupService.Lifecycle.cs:69`,
  `Services/WorkgroupService.Lifecycle.cs:88`), which throws on blank
  (`Services/WorkgroupService.Helpers.cs:124`).
- **A group is never registered without somewhere to work.** The Drive subfolder is created
  before the status flips (`Services/WorkgroupService.Lifecycle.cs:27`); a failure there throws
  and leaves the row untouched (`Services/WorkgroupService.Lifecycle.cs:254`).
- **Registration and application finish even if the browser goes away.** Both entry points
  overwrite their token before doing anything (`Services/WorkgroupService.cs:81`,
  `Services/WorkgroupService.Lifecycle.cs:21`, `Services/WorkgroupService.Lifecycle.cs:146`), and
  the folder call is made with an explicitly uncancellable token
  (`Services/WorkgroupService.Lifecycle.cs:248`).
- **Reactivation undoes Dormant completely.** Status, reason, end date and the written reasons
  all clear in one write (`Services/WorkgroupService.Lifecycle.cs:125`), and the folder goes
  writable again through a sync request (`Services/WorkgroupService.Lifecycle.cs:138`).
- **The daily pass never changes a group's status, and never persists a judgement about
  silence.** It measures elapsed quiet — `NudgeForUpdateAsync` reads `SilenceFor(now)` to decide
  whether the thirtieth day has come round (`Services/WorkgroupService.Rhythm.cs:50`) — and then
  only notifies and audits (`Services/WorkgroupService.Rhythm.cs:17`). What it must never do is
  turn that reading into state: a status assignment inside that file is a violation, and so is a
  column, a log entry or a flag recording that a group has gone quiet. Deciding a quiet group is
  finished is the Board's, on its own reading of the register.
- **A promised comment window is never cut short.** Delivering refuses while the window is still
  in the future (`Services/WorkgroupService.Documents.cs:162`), and so does a member ending the
  group (`Services/WorkgroupService.cs:471`). Closing early moves the end of the window to now
  and keeps the comments (`Services/WorkgroupService.Documents.cs:142`).
- **A comment window only ever opens on a Published document with categories that fit.** Draft
  and Delivered are refused (`Services/WorkgroupService.Documents.cs:100`), an empty category
  list is refused (`Services/WorkgroupService.Documents.cs:108`), and a category longer than the
  column a comment stores it in is refused
  (`Services/WorkgroupService.Documents.cs:112`) — otherwise the window would accept comments
  that could never be saved.
- **Delivered freezes the body.** Editing a Delivered document is refused
  (`Services/WorkgroupService.Documents.cs:59`), and a disposition may only be recorded on one
  (`Services/WorkgroupService.Lifecycle.cs:207`).
- **Commenting is gated by the window, not by membership.** Once the group is Active and the
  window is open, any signed-in human may comment
  (`Services/WorkgroupService.Documents.cs:195`); the category must be one the document declared
  (`Services/WorkgroupService.Documents.cs:198`).
- **A bulk answer never overwrites an individual one.** The per-category response touches only
  still-Pending, non-hidden comments in that category
  (`Services/WorkgroupService.Documents.cs:261`).
- **System log entries never change.** Editing or deleting a log entry checks the stored kind as
  well as the posted one (`Services/WorkgroupService.cs:402`,
  `Services/WorkgroupService.cs:421`) against the four kinds a human may write
  (`Services/WorkgroupService.Helpers.cs:115`).
- **A slug never shadows a route.** The reserve loop skips the reserved segments and any slug
  already taken (`Services/WorkgroupService.Helpers.cs:239`), and trims to the column's budget
  before suffixing (`Services/WorkgroupService.Helpers.cs:233`).
- **Only the repository touches the tables.** `WorkgroupRepository` is the one holder of
  `IDbContextFactory<WorkgroupsDbContext>` (`Data/WorkgroupRepository.cs:7`); no service file
  imports EF.
- **Moderation is auditable.** Hiding a comment writes an audit entry with the reason
  (`Services/WorkgroupService.Documents.cs:298`), because the row itself shows only "hidden by
  the group" to everyone but an admin.
- **Erasure keeps the association's record.** Attribution columns are nulled and membership rows
  deleted (`Data/WorkgroupRepository.cs:222`); comment bodies, log bodies, minutes and document
  bodies stay. An Active group whose only coordinator is this person is listed before the rows
  go (`Services/WorkgroupService.Gdpr.cs:123`) and the Board told from that list
  (`Services/WorkgroupService.Gdpr.cs:133`).

## 5. Seams

- **Coordinator is register-facing, not permission-facing.** The role is stored and displayed,
  and the authorization handler does not read it
  (`Authorization/WorkgroupAuthorizationHandler.cs:38`). A future "only a coordinator may X"
  rule has a place to live and nothing occupying it.
- **`WorkgroupLogKind.SurveySent` is declared and never written.** Only `SurveySubmitted` is
  (`Services/WorkgroupService.cs:449`). The send half of the survey lane is specified, reserved
  in the enum, and unbuilt.
- **`WorkgroupOperationRequirement.Read` has no production caller.** The handler answers it
  (`Authorization/WorkgroupAuthorizationHandler.cs:27`) and only the tests ask. The register is
  gated at the controller by `PolicyNames.AppAccess` instead, so the resource-level read
  question is defined and never asked.

## 6. Deliberately not done

- **No cross-section read interface and no `.Contracts` leaf.** Nothing outside the section
  names a Workgroups type; Calendar and GoogleIntegration are fan-outs Workgroups implements,
  and neither names it back. The decision follows the consumer list, never the name — a read
  interface arrives with its first consumer, not ahead of one.
- **No workflow engine.** Every transition is a human's, by resolution. There is no auto-register
  after fourteen days, no auto-close after the silence clock, and no state machine type — the
  transitions live as ordinary methods that check the current status and write the next one.
- **No permission level for Coordinator.** Above, under Seams: the distinction is on the
  register, not in the handler.
- **No uniqueness constraint on the slug.** Slug is editable display data; the service reserves
  it (`Services/WorkgroupService.Helpers.cs:225`) and the column carries a plain index. A unique
  index on an editable string trades a clear error for a database failure.
- **No concurrency token on any row.** One server, a few dozen rows, a volunteer Board —
  [`no-concurrency-tokens`](../../../../memory/architecture/no-concurrency-tokens.md).
- **No tombstone on a deleted log entry.** The audit trail is where a deletion stays visible
  (`Services/WorkgroupService.cs:425`); the log is a working record, not the immutable one.
- **No cross-section foreign key.** Every user id on every row is a bare `Guid` with an index at
  most.
- **No time-derived value inside the cached snapshot.** Everything that depends on "now" is an
  extension method computed at read time; a cached register therefore cannot carry a stale badge.
- **No architecture test project entry.** `tests/Humans.Workgroups.Tests` has no
  `Architecture/` folder, and none is owed. Arch tests live in the arch folder; where there are
  none there is no folder, and the absence is not a finding, a test to write or a question for
  Peter ([`no-tests-for-absences`](../../../../memory/architecture/no-tests-for-absences.md)).

## Load-bearing weirdness

- **`ct = CancellationToken.None` at the top of three methods is deliberate, not a leftover.**
  Apply, Register and RegisterExisting create a Drive folder and write rows that must not be
  half-done; the parameter is kept so the signature matches every sibling, and overwritten so a
  disconnecting browser cannot tear the write in half.
- **The nudge fires on exact multiples of the thirty-day window**
  (`Services/WorkgroupService.Rhythm.cs:59`). A pass that does not run on the right day skips
  that nudge entirely; the register badge is the durable signal and the notification is not.
- **A member may write a `StatusRequested` log entry directly.** `RequireMemberKind` admits it
  (`Services/WorkgroupService.Helpers.cs:112`) because the form's own kinds are member kinds,
  while the dedicated route (`Services/WorkgroupService.cs:194`) also notifies the coordinators.
  Two ways in; neither is rate-limited, and nothing downstream reads the entry.
- **`NoticeResources` is a hand-built `ResourceManager`, not the injected localizer**
  (`Services/WorkgroupService.Helpers.cs:279`). Notification titles are resolved per recipient
  language inside a background job where no request culture exists; a key that does not resolve
  renders as a bare colon and the group's name, with no error.
- **`WorkgroupsResource.cs` must stay in `namespace Humans.Workgroups`.** The SDK derives the
  resx manifest name from the adjacent same-named `.cs` file's namespace; a tidier one makes
  every string render as its raw key.
- **`Views/_ViewImports.cshtml` is not inherited from the Shell.** A missing `@using` there
  ships broken markup with a green build and no runtime error.

## History

| Date | Run | Headline |
|---|---|---|
| 2026-09-16 | section-doctor | First run. The invariant doc said the test project did not exist and denied the Surveys dependency it documents elsewhere; comments disagreed with the slug index and the member log kinds; the rhythm pass's per-group failure swallow was unpinned. |
