# Issues — Health

The target shape, regenerated each section-doctor run and diffed against the previous one.
Run reports live in `docs/health/runs/`.

## 1. What the section does

A human who hits a problem anywhere in the app files it here — a bug, a feature idea, a
question — optionally with a screenshot and the page they were on. The report lands in a
queue owned by whoever looks after that part of the app, and stays there until someone
closes it. Reporter and handler talk to each other in one thread on the report; the
reporter is emailed when a handler replies, the handler is pinged in-app when the reporter
does. A closed report reopens the moment its reporter says something else on it. Six months
after it closes for good, it and everything attached to it are deleted.

Agents and external integrations work the same queue through a machine door instead of the
browser. A key reaches exactly what the person who owns it reaches in the browser — no
further — and every write it makes is attributed to that person.

## 2. The shapes

| # | Question the section answers | Surface serving it |
|---|---|---|
| 1 | *What is in my queue?* | `GET /Issues` · `GetIssueListAsync` · `GetActionableCountForViewerAsync` · `GetDistinctReportersAsync` · `IssuesUserMenu` badge · admin-nav entry |
| 2 | *What is the story of this one?* | `GET /Issues/{id}` · `GetIssueByIdAsync` · `GetThreadAsync` |
| 3 | *I want to report something* | `GET /Issues/New` (form) · `POST /Issues` (submit) · `_IssueWidgetModal` · `SubmitIssueAsync` · `CreateIssueAsync` |
| 4 | *I want to say something on it* | `POST /Issues/{id}/Comments` · `PostCommentAsync` (auto-reopen, comment-and-resolve) |
| 5 | *I want to move one field on it* | `POST /Issues/{id}/{Status,Assignee,Section,GitHubIssue}` · `Update{Status,Assignee,Section}Async` · `SetGitHubIssueNumberAsync` · four `…WithResultAsync` twins |
| 6 | *Make it go away* | `PurgeExpiredAsync` + `CleanupIssuesJob` · `EraseForUserAsync` · `ContributeForUserAsync` |

Six questions. Shape 5 is one question wearing eight method signatures: every one of the four
fields is *load the issue, establish the viewer may handle it, compare, set, save, audit, maybe
notify, maybe invalidate*, and each is written twice — once throwing, once returning
`IssueMutationResult` — because the machine door wants the exception and the browser door wants
the message.

Every shape that answers *for* a viewer takes the asker with it — the queue, the badge count,
and every per-item read and mutation in shapes 2, 4 and 5. `IssueViewer` — the person's id plus
the role names they hold — is their one spelling of who is asking, and the section derives
everything about reach from it, including admin-ness. Nothing passes a privilege the caller
asserted (`memory/code/authorization-conventions.md`).

Three surfaces take no viewer, each for its own reason. Filing (shape 3) carries
`reporterUserId` and `actorUserId`: attribution, not authority, because any signed-in member may
file. Shape 6 acts for the system, not for a person — the retention job and the GDPR contributor
answer to a schedule and a data-subject request. And `GetDistinctReportersAsync` returns the
whole reporter list, gated by its one caller: `Index` asks for it only inside
`if (viewer.IsAdmin)`. That is the section's one reach decision made outside `IssuesService`.

## 3. Structure

- **`Domain/`** — `Issue`, `IssueComment`, and `IssueSectionRouting`, the section→roles table
  and the one statement of the handle rule (`CanHandle`).
- **`Data/`** — `IssuesDbContext` + the one repository. Repository projections are repo-local
  tuples/records; no service-layer DTO travels down into `Data/`.
- **`Services/`** — `IssuesService`: submit, thread assembly, badge count, retention, GDPR, and
  the four field mutations. It is the only enforcement point both doors share: `CanSee` gates
  every per-item read, `FindHandleableAsync` gates every mutation, and out-of-reach answers
  exactly what gone answers. **Target, not built:** the four mutations should be one *apply a
  field change* pipeline they parameterise, with result-vs-throw as a single wrapper — see §2
  shape 5 and §5.
- **`Controllers/` + `Models/` + `Views/`** — one controller, one page (list + inline detail),
  one submit form, one widget modal. View models carry only what a `.cshtml` renders. Every
  action builds its viewer through `ViewerFor` and reads admin-ness off it — never `User.IsInRole`
  and never a second viewer inline.
- **`Authorization/`** — one requirement (`Handle`) and its resource handler, which asks
  `IssueSectionRouting.CanHandle` the same question the service asks, to shape the page and to
  answer 403 where the service would answer 404.
- **Contracts leaf** — `IIssueTriage` (machine door), `IssueViewer` (who is asking),
  `IIssuesRetention` (job), `IssueStatus`, `IssueCategory`, the read models the machine door
  serialises.

## 4. Invariants

- An issue always has a reporter; the reporter can always read and comment on it.
  (`Data/Configurations/IssueConfiguration.cs:14`, `Services/IssuesService.cs:224`)
- The handle rule is stated once: Admin, or a holder of a role
  `IssueSectionRouting.RolesFor(Section)` names. A null section is Admin-only. Both the service
  and the browser's authorization handler read that one statement.
  (`Domain/IssueSectionRouting.cs:66`, `Authorization/IssuesAuthorizationHandler.cs:29`)
- Reach is the same at every door and for every shape: the queue lists only what the viewer may
  see, and a per-item read or mutation on anything else answers as if it did not exist — null
  from `GetIssueByIdAsync`, the same "not found" `InvalidOperationException` everywhere else. An
  id is not an oracle. (`Services/IssuesService.cs:210`, `:232`, `:269`, `:324`, `:392`)
- Only a handler may move a field. The reporter may read and comment on their own issue in any
  section, and may not resolve it — `resolveOnPost` from a non-handler is dropped, not refused.
  (`Services/IssuesService.cs:446`)
- A mutation is authorized against the section the issue is *in*, so a handler may route an
  issue out of their own queue. (`Services/IssuesService.cs:615`)
- A reporter's comment on a terminal issue reopens it to `Open` and clears `ResolvedAt` /
  `ResolvedByUserId`, with an audit row naming the reporter as actor.
  (`Services/IssuesService.cs:416`)
- `Section` may be changed only while the issue is non-terminal.
  (`Services/IssuesService.cs:619`)
- Every field change is audit-logged **after** the save, never before.
  (`Services/IssuesService.cs:505`, `:571`, `:631`, `:675`)
- Screenshots are JPEG/PNG/WebP and under 10 MB, or the submit fails.
  (`Services/IssuesService.cs:149`)
- Whenever a viewer's actionable count could have moved, both badge caches are dropped.
  (`Services/IssuesService.cs:178`, `:516`, `:636`)
- The activity thread has no table of its own: it is `issue_comments` merged with the
  `AuditAction.Issue*` rows at read time. (`Services/IssuesService.cs:375`)

## 5. Seams — specified, not built

- **The collapsed field-mutation pipeline** (§3). Eight method signatures answer one
  question-shape; the target is one parameterised pipeline behind one result wrapper.
  Collapsing them changes `IIssueTriage`, the machine door's surface, so it is Peter's call.
  The section's one English-only user-facing message rides with it: `UpdateSection`'s rejection
  reason reaches the page as the service's own string because the service has no way to return
  a resource key (`Controllers/IssuesController.cs:383`).
- Otherwise nothing in the section's docs or specs describes behavior that has not shipped.

## 6. Deliberately not done

- **No caching decorator.** Per-handler triage queues are not a hot read path; the only cache
  is the 2-minute per-viewer badge count the service owns directly.
- **No issue-events table.** The audit log is the event source for the thread; a parallel
  events schema would be a second truth.
- **No cross-section FK constraints and no navigation properties.**
  `Reporter`/`Assignee`/`ResolvedByUser`/`SenderUser` are bare `Guid` columns — no `HasOne<User>()`,
  no delete behaviour, nothing for EF to join across. Display names are stitched in memory via
  `IUserServiceRead`. Account deletion anonymises the `User` row in place, but that's moot here:
  the section's own GDPR contributor erases or nulls these ids first.
- **No `nameof`-derived audit discriminators.** `AuditEntityTypes.Issue` is a literal because
  it is persisted data.
- **No per-comment reporter/handler flag.** Sender role is derived by comparing
  `SenderUserId` to `Issue.ReporterUserId`.
- **No status-transition graph.** Any handler may set any status; the lifecycle in the docs is
  the intended path, not an enforced one.

## Load-bearing weirdness

- **`Section` is a free string, not an enum or FK.** The routing table is meant to change
  without a migration, so unknown values degrade to the Admin queue rather than failing.
- **The routing table outlives sections.** `Profiles` and `Legal` name sections that no longer
  exist; they stay routable while stored rows still carry those strings, and `SectionAnnotations`
  surfaces the drift on `/Debug/Sections` rather than hiding it.
- **`IssuesResource` is `public` and sits at the project root** beside its `.resx` files —
  the SDK derives the manifest name from the adjacent `.cs` file's namespace, and the boot
  localization diagnostic finds section markers via `GetExportedTypes()`.
- **The section's resource keys are `Issue_`-prefixed, not `Issues_`.** The conformance
  detector counts them as non-conforming; renaming them is churn across six cultures with no
  reader benefit, so they stay until a rename is asked for.
- **`DeleteByIdsAsync` loads then removes** instead of `ExecuteDeleteAsync`, because the
  EF InMemory provider used by the unit tests does not support the latter.
- **`SaveTrackedIssueAsync` attaches and force-marks Modified**, because the repository hands
  out entities from a context it has already disposed.
- **The comment-email path is one-directional by design.** Handler→reporter sends email;
  reporter→handler is in-app only, so handlers are not paged for every reply.
- **Authority and attribution are separate arguments.** `IssueViewer` says who may act;
  `actorUserId` / `senderUserId` say whose name goes on the audit row. They are the same person
  at both doors today, and `CreateIssueAsync` is why the pair exists: an agent may file an issue
  on someone else's behalf.

## History

| Date | Run | Reforge score | Notes |
|---|---|---|---|
| 2026-08-25 | [2026-08-25-Issues](../../../../docs/health/runs/2026-08-25-Issues.md) | 260 → 258 | First doctor run. Three user-visible defects fixed (case-sensitive search, unlocalized toasts, wrong attachment hint). PR: peterdrier/Humans#1499 |
| 2026-09-14 | [2026-09-14-Issues](../../../../docs/health/runs/2026-09-14-Issues.md) | | `IssueViewer` is now the only spelling of who is asking on every member that answers for a viewer; every status pill and the category filter speak all six cultures; the docs stopped describing cross-section foreign keys the section never configured. PR: peterdrier/Humans#1671 |
