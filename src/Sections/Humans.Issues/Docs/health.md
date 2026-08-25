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
browser, and every write they make is attributed to the human whose key was used.

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
fields is *load the issue, compare, set, save, audit, maybe notify, maybe invalidate*, and
each is written twice — once throwing, once returning `IssueMutationResult` — because the
machine door wants the exception and the browser door wants the message.

## 3. Structure

- **`Domain/`** — `Issue`, `IssueComment`, and `IssueSectionRouting`, the section→roles table.
- **`Data/`** — `IssuesDbContext` + the one repository. Repository projections are repo-local
  tuples/records; no service-layer DTO travels down into `Data/`.
- **`Services/`** — `IssuesService`: submit, thread assembly, badge count, retention, GDPR, and
  the four field mutations. **Target, not built:** those four should be one *apply a field
  change* pipeline they parameterise, with result-vs-throw as a single wrapper. Today it is
  four methods plus four `…WithResultAsync` try/catch copies — see §2 shape 5 and §5.
- **`Controllers/` + `Models/` + `Views/`** — one controller, one page (list + inline detail),
  one submit form, one widget modal. View models carry only what a `.cshtml` renders.
- **`Authorization/`** — one requirement (`Handle`) and its resource handler.
- **Contracts leaf** — `IIssueTriage` (machine door), `IIssuesRetention` (job), `IssueStatus`,
  `IssueCategory`, the read models the machine door serialises.

## 4. Invariants

- An issue always has a reporter; the reporter can always read and comment on it.
- Only Admin, or a holder of a role `IssueSectionRouting.RolesFor(Section)` names, may change
  any field or comment as a non-reporter. A null section is Admin-only.
- A non-handler sees exactly the issues they reported — no more, in any section.
- A reporter's comment on a terminal issue reopens it to `Open` and clears `ResolvedAt` /
  `ResolvedByUserId`, with an audit row naming the reporter as actor.
- `Section` may be changed only while the issue is non-terminal.
- Every field change is audit-logged **after** the save, never before.
- Screenshots are JPEG/PNG/WebP and under 10 MB, or the submit fails.
- Whenever a viewer's actionable count could have moved, both badge caches are dropped.
- The activity thread has no table of its own: it is `issue_comments` merged with the
  `AuditAction.Issue*` rows at read time.

## 5. Seams — specified, not built

- **The collapsed field-mutation pipeline** (§3). Eight method signatures answer one
  question-shape; the target is one parameterised pipeline behind one result wrapper.
  Collapsing them changes `IIssueTriage`, the machine door's surface, so it is Peter's call.
- Otherwise nothing in the section's docs or specs describes behavior that has not shipped.

## 6. Deliberately not done

- **No caching decorator.** Per-handler triage queues are not a hot read path; the only cache
  is the 2-minute per-viewer badge count the service owns directly.
- **No issue-events table.** The audit log is the event source for the thread; a parallel
  events schema would be a second truth.
- **No cross-domain navigation properties.** `Reporter`/`Assignee`/`ResolvedByUser`/`SenderUser`
  are FK columns only; display names are stitched in memory via `IUserServiceRead`.
- **No `nameof`-derived audit discriminators.** `AuditEntityTypes.Issue` is a literal because
  it is persisted data.
- **No per-comment reporter/handler flag.** Sender role is derived by comparing
  `SenderUserId` to `Issue.ReporterUserId`.
- **No status-transition graph.** Any handler may set any status; the lifecycle in the docs is
  the intended path, not an enforced one.

## Load-bearing weirdness

- **`Section` is a free string, not an enum or FK.** The routing table is meant to change
  without a migration, so unknown values degrade to the Admin queue rather than failing.
- **`IssuesResource` is `public` and sits at the project root** beside its `.resx` files —
  the SDK derives the manifest name from the adjacent `.cs` file's namespace, and the boot
  localization diagnostic finds section markers via `GetExportedTypes()`.
- **`DeleteByIdsAsync` loads then removes** instead of `ExecuteDeleteAsync`, because the
  EF InMemory provider used by the unit tests does not support the latter.
- **`SaveTrackedIssueAsync` attaches and force-marks Modified**, because the repository hands
  out entities from a context it has already disposed.
- **The comment-email path is one-directional by design.** Handler→reporter sends email;
  reporter→handler is in-app only, so handlers are not paged for every reply.

## History

| Date | Run | Reforge score | Notes |
|---|---|---|---|
| 2026-08-25 | [2026-08-25-Issues](../../../../docs/health/runs/2026-08-25-Issues.md) | 260 → 258 | First doctor run. Three user-visible defects fixed (case-sensitive search, unlocalized toasts, wrong attachment hint). PR: peterdrier/Humans#1499 |
