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
| 5 | *I want to move one field on it* | `POST /Issues/{id}/{Status,Assignee,Section,GitHubIssue}` · `Update{Status,Assignee,Section}Async` · `SetGitHubIssueNumberAsync` |
| 6 | *Make it go away* | `PurgeExpiredAsync` + `CleanupIssuesJob` · `EraseForUserAsync` · `ContributeForUserAsync` |

Shape 5 is the field mutations over one pipeline: find the issue the viewer may handle, apply the
change, save, audit, notify. Missing (or out of reach), rule-refused, succeeded and failed are the
outcomes, and both doors read them from the same `IssueMutationResult`.

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

- **`Domain/`** — `Issue`, `IssueComment`, and `IssueSectionRouting`, the lookup over the queues
  sections declared through `IIssueQueueOwner` and the one statement of the handle rule
  (`CanHandle`).
- **`Data/`** — `IssuesDbContext` + the one repository. Repository projections are repo-local
  tuples/records; no service-layer DTO travels down into `Data/`.
- **`Services/`** — `IssuesService`: submit, thread assembly, badge count, retention, GDPR, and
  the field mutations behind one private pipeline. It is the only enforcement point both
  doors share: `CanSee` gates every per-item read, `FindHandleableAsync` gates every mutation,
  and out-of-reach answers exactly what gone answers.
- **`Controllers/` + `Models/` + `Views/`** — one controller, one page (list + inline detail),
  one submit form, one widget modal. View models carry only what a `.cshtml` renders. Every
  action builds its viewer through `ViewerFor` and reads admin-ness off it — never `User.IsInRole`
  and never a second viewer inline. **Target, not built:** the Area label and the
  page-path → queue guess come from the sections that own the queues, not from hand lists in
  `Models/` (`AreaLabelMap`, `IssueSectionInference`) that name sections by hand.
- **`Authorization/`** — one requirement (`Handle`) and its resource handler, which asks
  `IssueSectionRouting.CanHandle` the same question the service asks, to shape the page and to
  answer 403 where the service would answer 404.
- **Contracts** — `IIssueTriage` (machine door), `IssueViewer` (who is asking),
  `IIssueQueueOwner` (a section declaring a queue), `IIssuesRetention` (job), `IssueStatus`,
  `IssueCategory`, the read models the machine door serialises.

## 4. Invariants

- An issue always has a reporter; the reporter can always read and comment on it.
  (`Data/Configurations/IssueConfiguration.cs:14`, `Services/IssuesService.cs:246`)
- The handle rule is stated once: Admin, or a holder of a role
  `IssueSectionRouting.RolesFor(Section)` names. A null section is Admin-only. Both the service
  and the browser's authorization handler read that one statement.
  (`Domain/IssueSectionRouting.cs:69`, `Authorization/IssuesAuthorizationHandler.cs:26`)
- Reach is the same at every door and for every shape: the queue lists only what the viewer may
  see, and a per-item read or mutation on anything else answers as if it did not exist — null
  from `GetIssueByIdAsync`, a missing result from every mutation, the "not found"
  `InvalidOperationException` from `GetThreadAsync`. An id is not an oracle.
  (`Services/IssuesService.cs:231`, `:256`, `:292`, `:345`, `:413`)
- Only a handler may move a field. The reporter may read and comment on their own issue in any
  section, and may not resolve it — `resolveOnPost` from a non-handler is dropped, not refused.
  (`Services/IssuesService.cs:466`)
- A mutation is authorized against the section the issue is *in*, so a handler may route an
  issue out of their own queue. (`Services/IssuesService.cs:602`)
- A reporter's comment on a terminal issue reopens it to `Open` and clears `ResolvedAt` /
  `ResolvedByUserId`, with an audit row naming the reporter as actor.
  (`Services/IssuesService.cs:436`)
- A new issue and a reporter's comment notify the queue's role-holders; Admins are notified
  only when no role owns the queue. (`Services/IssuesService.cs:964`)
- `Section` may be changed only while the issue is non-terminal.
  (`Services/IssuesService.cs:616`)
- An unrouted section value is stored as null (the Admin queue), never refused; only an
  over-length value fails. (`Services/IssuesService.cs:100`)
- Every field change is audit-logged **after** the save, never before.
  (`Services/IssuesService.cs:533`, `:583`, `:629`, `:652`)
- Screenshots are JPEG/PNG/WebP and under 10 MB, or the submit fails.
  (`Services/IssuesService.cs:156`)
- The actionable badge counts `Triage` and `Open` only — `InProgress` needs no picking up.
  (`Data/IssuesRepository.cs:110`)
- Whenever a viewer's actionable count could have moved, both badge caches are dropped.
  (`Services/IssuesService.cs:197`, `:544`, `:634`)
- The activity thread has no table of its own: it is `issue_comments` merged with the
  `AuditAction.Issue*` rows at read time. (`Services/IssuesService.cs:396`)

## 5. Seams — specified, not built

- None. Nothing in the section's docs or specs describes behavior that has not shipped.

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
- **No `IssueViewer.From(ClaimsPrincipal)`.** The contracts stay free of ASP.NET types; the
  controller's private `ViewerFor` is the one builder.

## Load-bearing weirdness

- **`Section` is a free string, not an enum or FK.** The routing table is meant to change
  without a migration, so unknown values degrade to the Admin queue rather than failing.
- **The routing table outlives sections.** `Profiles` and `Legal` name sections that no longer
  exist; Users and Consent declare them under the spelling their stored rows carry, so they stay
  routable, and `/Debug/Sections` lists them as unmatched annotations rather than hiding the drift.
- **`IssuesResource` is `public` and sits at the project root** beside its `.resx` files —
  the SDK derives the manifest name from the adjacent `.cs` file's namespace, and the boot
  localization diagnostic finds section markers via `GetExportedTypes()`.
- **The section's resource keys are `Issue_`-prefixed, not `Issues_`.** Exempt by Peter's ruling
  (peterdrier/Humans#1682); both conformance detectors skip it.
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
| 2026-10-11 | [2026-10-11-Issues](../../../../docs/health/runs/2026-10-11-Issues.md) | | The field mutations run through one pipeline; badge eviction on status, reopen and section change, source-section routing and the reporter-reopen audit are now pinned by tests; the docs stopped claiming Issues names no queue by hand. PR: peterdrier/Humans#1954 |
