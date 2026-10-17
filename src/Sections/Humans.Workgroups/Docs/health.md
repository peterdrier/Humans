<!-- freshness:triggers
  src/Sections/Humans.Workgroups/**
  tests/Humans.Workgroups.Tests/**
-->

# Workgroups — Health

Last assessed: 2026-10-17 (section-doctor).

## 1. What the section does

Keeps the book of working groups the association has recognised, and the record of what each
one did.

Somebody with an idea fills in a short form: what the group is for, what it will hand over, to
whom, and by when. The Secretary looks at it and either recognises it or does not — nothing here
decides that by itself. Once recognised, the group gets a page, a shared folder, and a place to
write down what it is doing: minutes of its meetings, notes and updates in a running log,
drafts of whatever it is producing. Anyone may ask to join; the group's coordinators let people
in. A group may call itself sensitive, and then its roster, documents and history are for its
members and the Board only. A draft is the group's own until it publishes it. Once something is
published, people may say something about it while its window for remarks is open. The Board
may give a group a budget, which opens an expense account in the books for it. When the group is
finished — or has been quiet long enough that the Secretary closes it — its page stays readable
for good.

Nothing here acts on its own. Once a day the system looks for groups that have gone quiet and
applications nobody has answered, and tells the people responsible. That is the whole of its
initiative: it writes notes, raises flags, and waits for a human.

## 2. The shapes

| Shape | The question | Surfaces |
|---|---|---|
| **The register** | What groups exist, what state are they in, and what do they owe? | `GET /Workgroups`, `GET /Workgroups/{slug}`, the Governance card, the dashboard card, the admin tile, the things-to-do entries |
| **Recognise it** | Should this group exist, and does it still? | `GET/POST /Workgroups/Apply`, `POST /Workgroups/Admin/{id}/Register`, `/Refer`, `/Refuse`, `/Withdraw`, `/Close`, `/Reactivate`, `GET/POST /Workgroups/Admin/RegisterExisting`, `POST /Workgroups/{slug}/Done` |
| **Who is in it** | Who is working on this, who let them in, and who speaks for it? | `POST /Workgroups/{slug}/Join`, `/Leave`, `/Members/{userId}/Approve`, `/Members/{userId}/Decline`, `/Members/Add`, `/Coordinators`, `POST /Workgroups/Admin/{id}/Coordinators` |
| **Describe it** | What does the group say about itself? | `GET/POST /Workgroups/{slug}/Edit` |
| **What did it do** | What happened, and when? | `/Log/Save`, `/Log/{id}/Delete`, `/Meetings/Save`, `/Meetings/{id}/Delete`, `/Surveys/Link`, `/RequestStatus` |
| **What did it produce** | What is the group handing over, and what did people say about it? | `GET /Workgroups/{slug}/Documents/{id}`, `/Documents/Save`, `/Publish`, `/OpenComments`, `/CloseComments`, `/Deliver`, `/Comments`, `/Comments/RespondCategory`, `/Comments/{id}/Respond`, `/Comments/{id}/Hide` |
| **The Board's reply** | What did the Board decide about what it received? | `POST /Workgroups/Admin/Documents/{id}/Disposition` |
| **What may it spend** | How much is allocated, and against which account? | `POST /Workgroups/Admin/{id}/Budget` |
| **Nudge the humans** | Who is behind, and who needs telling? | `RunDailyRhythmAsync` through `WorkgroupRhythmJob`, plus the read-time badges in `WorkgroupRhythm` |
| **Where do the files live** | Which Drive folder is this group's, and who may open it? | `GET/POST /Workgroups/Admin/Settings`, `WorkgroupDriveAccessSource`, `WorkgroupCalendarContributor` |
| **What do we hold on this person** | Export, erasure, account merge. | `ContributeForUserAsync`, `EraseForUserAsync`, `ReassignAsync` |

"Is this group allowed to do X?" is not a shape. It is two questions: the service asks whether
the group is Active, and — for membership decisions — whether the actor coordinates it; the
controller asks the authorization handler whether the actor is a member or the Board.

## 3. Structure

A register with a rhythm on top. Written fresh, it is:

- **One table family**: a row per group with five child collections, plus a one-row settings
  table, reached through a single repository that hands back the whole register as one detached
  graph. Nothing pages; the dataset is a few dozen rows.
- **One service**, split into partials along the shapes: the register's own rules, the
  Secretary's decisions, the documents and their comment window, the daily pass, the GDPR
  fan-outs, and a helpers file with the projection, the guards and each crosscut's one call site.
  Every rule failure comes back as a refusal value naming a resource key; nothing throws for one.
- **A pure-function rhythm module** over the projected record — no clock, no dependencies — so
  the daily job and the page badges answer the same questions from the same code.
- **Two controllers**, one per audience: the member-facing group page and the Secretary's queue.
  Each parses, authorizes, calls, and turns a refusal into a message.
- **One caching decorator** holding the whole register under one key, cleared on every write,
  with nothing time-derived inside it.
- **Two inbound contributors** — the calendar feed and the Drive access source — that other
  sections call without naming Workgroups.

The layout matches with one exception: the document methods load through their own loader and
then check the group is Active by hand at each site, because the Board's disposition shares that
loader and must not be gated. The group-loading methods take the check from the loader.

## 4. Invariants

- **Member work happens only on an Active group.** Every member-facing write returns
  `RequireAcceptsMemberWork`'s refusal unless the status is Active
  (`Services/WorkgroupService.Helpers.cs:126`). A member-facing write that skips it is a
  violation.
- **Letting people in is the coordinators' call.** Joining only files a pending request
  (`Services/WorkgroupService.cs:138`); approving, declining and adding refuse anyone who is not
  a current coordinator unless the caller acts as Board or Admin
  (`Services/WorkgroupService.Helpers.cs:134`).
- **One or two coordinators, never zero by accident.** Setting coordinators refuses a count
  outside one-to-two (`Services/WorkgroupService.cs:394`) and any id that is not a current,
  non-pending member (`Services/WorkgroupService.cs:398`); the last coordinator leaving must name
  a replacement unless the caller acts as admin (`Services/WorkgroupService.cs:283`).
- **The Secretary's refusals are written down.** Refuse, Withdraw and Close call `RequireReasons`
  before anything else (`Services/WorkgroupService.Lifecycle.cs:81`,
  `Services/WorkgroupService.Lifecycle.cs:105`, `Services/WorkgroupService.Lifecycle.cs:135`),
  which refuses blank (`Services/WorkgroupService.Helpers.cs:163`).
- **A group is never registered without somewhere to work.** The Drive subfolder is created
  before the status flips (`Services/WorkgroupService.Lifecycle.cs:28`); a failure there returns
  a refusal and leaves the row untouched (`Services/WorkgroupService.Lifecycle.cs:298`).
- **A decision, once committed, finishes.** After the first write each mutation drops its
  cancellation token, so the log entry, audit row and notices that follow cannot be torn off by a
  closed tab (`Services/WorkgroupService.Helpers.cs:561`); Register drops it before it starts
  (`Services/WorkgroupService.Lifecycle.cs:20`).
- **Reactivation undoes Dormant completely.** Status, reason, end date and the written reasons
  clear in one write (`Services/WorkgroupService.Lifecycle.cs:158`).
- **The daily pass never changes a group's status, and never persists a judgement about
  silence.** It reads `SilenceFor(now)` to decide whether a thirty-day multiple has come round
  (`Services/WorkgroupService.Rhythm.cs:50`) and then only notifies and audits. A status
  assignment in that file is a violation, and so is a column, log entry or flag recording that a
  group has gone quiet.
- **A promised comment window is never cut short.** Delivering refuses while the window is still
  open (`Services/WorkgroupService.Documents.cs:183`), and so does a member ending the group
  (`Services/WorkgroupService.cs:628`). Closing early moves the end of the window to now and keeps
  the comments (`Services/WorkgroupService.Documents.cs:158`).
- **A comment window only opens on a Published document with categories that fit.** Anything
  else is refused (`Services/WorkgroupService.Documents.cs:112`), so is an empty category list
  (`Services/WorkgroupService.Documents.cs:120`) and a category longer than the column a comment
  stores it in (`Services/WorkgroupService.Documents.cs:124`).
- **Delivered freezes the body.** Editing a Delivered document is refused
  (`Services/WorkgroupService.Documents.cs:63`), and a disposition may only be recorded on one
  (`Services/WorkgroupService.Lifecycle.cs:263`).
- **Commenting is gated by the window, and for a sensitive group by membership.** A sensitive
  group refuses comments from non-members (`Services/WorkgroupService.Documents.cs:220`);
  otherwise any signed-in human may comment while the window is open
  (`Services/WorkgroupService.Documents.cs:225`), in a category the document declared
  (`Services/WorkgroupService.Documents.cs:229`).
- **A sensitive group's content stays inside it.** Its documents 404 for anyone but members and
  the Board (`Controllers/WorkgroupsController.cs:311`), its roster and history render only for
  them (`Models/WorkgroupViewModels.cs:129`), and its meetings never reach the public calendar
  (`Services/Contributors/WorkgroupCalendarContributor.cs:37`).
- **A bulk answer never overwrites an individual one.** The per-category response touches only
  still-Pending, non-hidden comments (`Services/WorkgroupService.Documents.cs:297`).
- **System log entries never change.** Editing or deleting checks the stored kind as well as the
  posted one (`Services/WorkgroupService.cs:544`, `Services/WorkgroupService.cs:573`) against
  the four kinds a human may write (`Services/WorkgroupService.Helpers.cs:153`).
- **A slug never shadows a route.** The reserve loop skips the reserved segments and any slug
  already taken (`Services/WorkgroupService.Helpers.cs:290`), after trimming to the column's
  budget (`Services/WorkgroupService.Helpers.cs:285`).
- **Only the repository touches the tables.** `WorkgroupRepository` is the one holder of
  `IDbContextFactory<WorkgroupsDbContext>` (`Data/WorkgroupRepository.cs:7`).
- **Moderation is auditable.** Hiding a comment writes an audit entry with the reason
  (`Services/WorkgroupService.Documents.cs:337`).
- **Erasure keeps the association's record.** Attribution columns are nulled and membership rows
  deleted (`Data/WorkgroupRepository.cs:229`); bodies stay. An Active group whose only
  coordinator is this person is listed before the rows go
  (`Services/WorkgroupService.Gdpr.cs:135`) and the Board told from that list
  (`Services/WorkgroupService.Gdpr.cs:142`).

## 5. Seams

- **`WorkgroupLogKind.SurveySent` is declared and never written.** Only `SurveySubmitted` is
  (`Services/WorkgroupService.cs:603`). The send half of the survey lane is reserved, not built.
- **`WorkgroupOperationRequirement.Read` has no production caller.** The handler answers it and
  only the tests ask; the register is gated by `PolicyNames.AppAccess` at the controller.

## 6. Deliberately not done

- **No cross-section read interface and no `.Contracts` leaf.** Nothing outside the section names
  a Workgroups type; Calendar and GoogleIntegration call fan-outs Workgroups implements. A read
  interface arrives with its first consumer.
- **No workflow engine.** Every transition is a human's. No auto-register, no auto-close, no
  state-machine type — ordinary methods check the current status and write the next one.
- **Coordinator is not an authorization level.** The handler answers member-or-Board; the
  coordinator check for membership decisions lives in the service, next to the rule it guards.
- **No uniqueness constraint on the slug.** The service reserves it and the column carries a
  plain index; a unique index on an editable string trades a clear refusal for a database error.
- **No grant revocation when the root Drive folder changes.** The root is set once at rollout;
  Peter declined reconciling a replacement on peterdrier/Humans#1650.
- **No concurrency token on any row** —
  [`no-concurrency-tokens`](../../../../memory/architecture/no-concurrency-tokens.md).
- **No tombstone on a deleted log entry.** The audit trail is where a deletion stays visible.
- **No cross-section foreign key.** Every user id is a bare `Guid` with an index at most.
- **No time-derived value inside the cached snapshot.** Everything that depends on "now" is
  computed at read time, so a cached register cannot carry a stale badge.
- **No architecture test folder.** None is owed
  ([`no-tests-for-absences`](../../../../memory/architecture/no-tests-for-absences.md)).

## Load-bearing weirdness

- **`ct = CancellationToken.None` mid-method is deliberate.** It marks the commit boundary in
  each mutation (above, under Invariants); the parameter is kept so every signature matches.
- **The nudge fires on exact multiples of the thirty-day window**
  (`Services/WorkgroupService.Rhythm.cs:56`). A pass that misses the day skips that nudge; the
  register badge is the durable signal, the notification is not.
- **A member may write a `StatusRequested` log entry directly.** `RequireMemberKind` admits it,
  while the dedicated `/RequestStatus` route also notifies the coordinators. Two ways in; nothing
  downstream reads the entry.
- **`NoticeResources` is a hand-built `ResourceManager`, not the injected localizer**
  (`Services/WorkgroupService.Helpers.cs:330`). Notification titles resolve per recipient
  language inside a background job with no request culture.
- **Retired folders stay claimed with no grants.** The Drive source keeps every non-Active,
  non-Dormant group's folder in its claim set with an empty grant list
  (`Services/Contributors/WorkgroupDriveAccessSource.cs:35`), so reconciliation strips direct
  grants rather than forgetting the folder.
- **`WorkgroupsResource.cs` must stay in `namespace Humans.Workgroups`.** The SDK derives the resx
  manifest name from it; a tidier namespace renders every string as its raw key.
- **`Views/_ViewImports.cshtml` is not inherited from the Shell.** A missing `@using` there ships
  broken markup with a green build.

## History

| Date | Run | Headline |
|---|---|---|
| 2026-09-16 | section-doctor | First run. The invariant doc said the test project did not exist and denied the Surveys dependency it documents elsewhere; comments disagreed with the slug index and the member log kinds; the rhythm pass's per-group failure swallow was unpinned. |
| 2026-10-17 | section-doctor ([run](../../../../docs/health/runs/2026-10-17-Workgroups.md), PR pending) | The section outgrew its target: join approval, sensitive groups and budgets shipped and guards return refusals. Docs and comments caught up; the Active guard and the slug lookup collapsed; unpinned invariants got tests. |
