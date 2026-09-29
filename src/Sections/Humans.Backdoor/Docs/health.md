<!-- freshness:triggers
  src/Sections/Humans.Backdoor/**
  tests/Humans.Backdoor.Tests/**
-->

# Backdoor — Target Shape

Derived fresh each section-doctor run, before any scan. What the section *should* be, not a
summary of what it is.

## 1. What the section does

Hands a named human a personal key, and lets whatever they run — an agent, a script, a laptop —
talk to the app as them. An admin allocates a key on one page; the plaintext appears once and
never again. Every request carrying that key is attributed to the person it was issued to, so a
machine's writes land in the audit thread with a real name on them.

The key stops working the moment its holder stops being an active Admin or Board member. It is
not deleted when that happens — it is refused, so restoring the role or lifting the suspension
brings it back.

What the key opens is a fixed set of read and write surfaces borrowed from other parts of the
app: the in-memory log tail, agent conversation transcripts, the issue queue, the feedback
queue, survey definitions with their responses and aggregates, the Store's accounting export,
the bookkeeping side of expenses and Holded, and the holder's own notification inbox. None of
that data is this section's; it owns only the keys. Where the browser would scope or gate a
page by who is looking, the key is scoped and gated the same way — as its holder.

## 2. The shapes

The section's external surface, grouped by the question each endpoint answers rather than
listed. The grouping is what makes collapse and duplication visible.

| # | Shape | Where it appears | Notes |
|---|---|---|---|
| S1 | **Credential lifecycle** — allocate, rotate, revoke, list | `/Backdoor` | The section's own domain. HTML, cookie-authed, Admin-only |
| S2 | **Authenticate a machine** — presented secret → a person with roles | one authorization filter | The single gate, class-scoped on every machine controller, so all of S3–S9 hangs off it |
| S3 | **List a queue or a register, filtered** — parse and clamp query, delegate, sort, project | logs, agent conversations, issues, feedback, surveys, store order lines and payments, expense reports, creditor accounts, category map, SEPA transfers, notifications | One shape |
| S4 | **Fetch one item in full** — delegate, 404 on missing, project | issue, feedback report, conversation, survey definition and aggregates, expense report, creditor ledger, Holded sync state | One shape |
| S5 | **Fetch one item's sub-collection** — re-fetch parent, 404, project the collection | issue comments, feedback messages, conversation messages, survey responses | The parent re-fetch is the price of a 404 |
| S6 | **Append to an item's thread** — validate model, delegate, echo the new row | issue comment, feedback message | One shape |
| S7 | **Patch one field on an item** — delegate `(id, value, actor)`, `{success:true}`, 404 on missing, 422 on rejected | issue status / assignee / section / github-issue; feedback status / assignment / github-issue | One `PatchAsync` pipeline per controller |
| S8 | **Create an item** | issue create | |
| S9 | **Stream stored bytes** — authorize against the owning item, return the file | expense-report attachment | |
| F1 | **User-data fan-outs** — export slice, erasure, merge fold | the key service | Owed because `backdoor_api_keys` is user-keyed |

What follows from the table drives everything below:

- **S1 + S2 + F1 is the whole of Backdoor's own logic.** Everything under S3–S9 is translation:
  parse, authorize through the served section's own policy, delegate to that section's
  contracts interface, shape JSON. A rule that lives in a controller here is a rule in the wrong
  section.
- **Authorization on S3–S9 is borrowed, never invented.** Where a surface is gated, the gate is
  the served section's own scoping (a viewer passed to Issues, a principal passed to
  Notifications) or its own named policy (Finance, Expenses) — never a Backdoor-local role test.
- **The expense report is one projection with a detail tail.** List and detail emit the same
  fields under the same audience split; detail adds the payment half and the lines.

## 3. Structure

The layout those shapes imply, written fresh:

```
Contracts/          one constant the Shell reads: the auth scheme name
Filters/            one authorization filter — S2
Services/           the key service + its result/row records, the audit discriminators — S1, F1
Data/               one repository over one table, one context, one config, one migration
Domain/             one entity
Models/             one view model for the one page
Controllers/        BackdoorController              — S1, HTML, Admin cookie
                    BackdoorLogsController          — S3 over Base's log sink
                    BackdoorAgentController         — S3, S4, S5
                    BackdoorSurveysController       — S3, S4, S5
                    BackdoorIssuesController        — S3, S4, S5, S6, S7, S8
                    BackdoorFeedbackController      — S3, S4, S5, S6, S7
                    BackdoorStoreController         — S3
                    BackdoorFinanceController       — S3, S4, S9
                    BackdoorNotificationsController — S3
Views/Backdoor/     one page
```

The structural rules the layout has to keep:

- **The project references exactly the assemblies its types come from.** Backdoor is a leaf that
  reaches the sections it serves; every one of those references is load-bearing or it is not
  there.
- **A controller takes only what it uses.** A machine controller that reads no user data takes
  no user service and sits on `ControllerBase`; the base class that exists to resolve users is
  for controllers that resolve users.
- **A request-shaping default that can never fire is not a safety net.** S2 guarantees a
  principal before any S3–S9 body runs; a controller that also carries a fallback for its
  absence is describing a state the filter forbids.

## 4. Invariants

Stated so a violation is recognisable; paths are relative to `src/Sections/Humans.Backdoor/`.

- A presented key resolves to exactly one person, and that person becomes the request
  principal — id plus active roles, under the Backdoor scheme — at
  `Filters/BackdoorApiKeyAuthFilter.cs:59`. No key and an unresolvable key are the same bare
  401 (`Filters/BackdoorApiKeyAuthFilter.cs:34`, `Filters/BackdoorApiKeyAuthFilter.cs:41`). At
  most one row can match a hash: the unique index at
  `Data/Configurations/BackdoorApiKeyConfiguration.cs:17`.
- The database never holds a plaintext key: issue and rotate persist only the hash and a
  12-character prefix (`Services/BackdoorApiKeyService.cs:166`), and resolution looks up by hash
  (`Services/BackdoorApiKeyService.cs:121`).
- A key authenticates only while its owner is **both** in Admin or Board **and** in
  `UserState.Active` — the one test at `Services/BackdoorApiKeyService.cs:143`, applied at issue
  (`Services/BackdoorApiKeyService.cs:48`), at rotate (`Services/BackdoorApiKeyService.cs:88`)
  and on every request (`Services/BackdoorApiKeyService.cs:124`). Failing it refuses the key and
  never revokes it.
- A rotate revokes the old row and inserts its replacement in one save, or writes neither —
  `Data/BackdoorApiKeyRepository.cs:51`.
- Issue and revoke each write one audit entry naming the key and its owner
  (`Services/BackdoorApiKeyService.cs:174`); a rotate is a revoke entry followed by an issue
  entry (`Services/BackdoorApiKeyService.cs:97`).
- Every issue and feedback write passes the key owner as the acting user
  (`Controllers/BackdoorIssuesController.cs:34`, `Controllers/BackdoorFeedbackController.cs:32`).
- An issue is read, commented on and patched as its holder — id and roles — through the viewer
  Issues scopes by (`Controllers/BackdoorIssuesController.cs:42`); the notification inbox is read
  for the installed principal (`Controllers/BackdoorNotificationsController.cs:30`).
- Finance-wide routes pass `FinanceAdminOrAdmin` and report routes pass Expenses' own
  `ExpenseReportView` policy, both through `IAuthorizationService`
  (`Controllers/BackdoorFinanceController.cs:291`, `Controllers/BackdoorFinanceController.cs:294`);
  a refusal is a 403.
- No raw IBAN leaves the finance surface: every JSON string and every download filename is
  scrubbed after the action (`Controllers/BackdoorFinanceController.cs:248`).
- A key-authed principal skips the Shell's onboarding gates rather than being redirected to HTML
  (`src/Humans.Web/Authorization/MembershipRequiredFilter.cs:84`,
  `src/Humans.Web/Authorization/NameRequiredFilter.cs:72`).
- Erasure hard-deletes the person's own keys and detaches them from anyone else's as both
  creator and revoker (`Data/BackdoorApiKeyRepository.cs:82`); merge re-points every one of
  those columns onto the survivor (`Data/BackdoorApiKeyRepository.cs:99`).

## 5. Seams

Specified-but-unbuilt work. Not built here, not ranked — reserved so items touching it are
shaped by it.

- **Per-key scope.** A key is all-or-nothing across every surface it opens. Nothing in the model
  says a key could be read-only or single-surface, and nothing has asked for it.
- **Role scoping on the unscoped reads.** Agent, feedback, logs, store and surveys serve every
  eligible key the same rows; nothing narrows them by the holder's roles, and no spec asks for it.

## 6. Deliberately not done

- **No caching decorator on the key service.** A cache would have to be invalidated on every
  revoke to stay correct about the one thing that matters most, and lookups are a single indexed
  hash probe at a handful of requests per minute.
- **No resource set.** Every string on the one page is English admin plumbing, read only by full
  Admins — the same call Debug makes.
- **No shared base controller for the machine surface.** A controller per served section, over
  shapes they share, looks like a base class; it would be one, in Base, serving one section, and
  would put the patch pipeline further from the rules it enforces. Collapse within a controller,
  not across them.
- **No auto-revocation on ineligibility.** Refusal is reversible and a transient role gap must
  not destroy a credential.
- **No FK constraints on the user columns.** Cross-section Guids by rule.
- **No Backdoor-local authorization rule.** A gate here would be a second copy of a rule the
  served section owns, free to drift from the browser's.

## Load-bearing weirdness

Essential complexity and settled decisions, so later runs stop re-litigating them:

- **The section exists to hold routes that came from, or serve, other sections.** Consolidating
  every key-authed route here is what made one auth model possible; the controllers living away
  from the data they serve is the point, not drift.
- **`Humans.Backdoor` references the section assemblies it serves.** That is legal precisely
  because each is reached only through a public contracts interface and nothing references
  Backdoor back — the graph stays acyclic because this section is a leaf.
- **The account-state half of eligibility is not redundant with the role half.** Suspension
  moves `users.State` and deliberately leaves role assignments standing, so a role-only test
  would keep authenticating a suspended admin's key.
- **`CreatedByUserId` is nullable.** Not because issuing is optional, but so GDPR erasure can
  detach a deleted admin from a key that still belongs to someone else.
- **The finance controller re-serializes its own output.** The IBAN scrub turns every result into
  a JSON tree and masks each string, so a field added later cannot forget to — the cost is one
  extra serialization per response on a handful of calls a day.
- **A finance refusal is a 403, an issue refusal a 404.** Issues hides an id outside the holder's
  queue; the finance-wide routes already disclose that the registers exist, so their report and
  attachment routes say "denied" rather than "missing".
- **The migrations-history table is `__EFMigrationsHistory_Backdoor`.** One database, one
  connection; the split is a code-side partition of the EF model.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| section-doctor | 2026-09-03 | First doctoring: unclamped `?limit=` reaching SQL, a dead project reference, and the untested feedback controller | peterdrier/Humans#1586 |
| section-doctor | 2026-09-29 | Store and Surveys machine controllers shed an unused user service; filter wiring pinned on every machine controller; drift from the finance, store and notifications additions cleared | pending |
