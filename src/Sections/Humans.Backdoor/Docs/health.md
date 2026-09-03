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

What the key opens is a small, fixed set of read and write surfaces borrowed from other
parts of the app plus the log tail: the in-memory logs, agent conversation transcripts, the
issue queue, the feedback queue, and survey definitions with their responses and aggregates.
None of that data is this section's; it owns only the keys.

## 2. The shapes

The section's external surface, grouped by the question each endpoint answers rather than
listed. The grouping is what makes collapse and duplication visible.

| # | Shape | Where it appears | Notes |
|---|---|---|---|
| S1 | **Credential lifecycle** — allocate, rotate, revoke, list | `/Backdoor` | The section's own domain. HTML, cookie-authed, Admin-only |
| S2 | **Authenticate a machine** — presented secret → a person | one authorization filter | The single gate; the filter is class-scoped on every machine controller, so all of S3–S8 hangs off it |
| S3 | **List a queue, filtered** — parse and clamp query, delegate, project | logs, agent conversations, issues, feedback, surveys | One shape |
| S4 | **Fetch one item in full** — delegate, 404 on missing, project | issue, feedback report, conversation, survey definition, survey aggregates | One shape |
| S5 | **Fetch one item's sub-collection** — re-fetch parent, 404, project the collection | issue comments, feedback messages, conversation messages, survey responses | The parent re-fetch is the price of a 404 |
| S6 | **Append to an item's thread** — validate model, delegate, echo the new row | issue comment, feedback message | One shape |
| S7 | **Patch one field on an item** — delegate `(id, value, actor)`, `{success:true}`, 404 on missing, 422 on rejected | issue status / assignee / section / github-issue; feedback status / assignment / github-issue | **One shape** — one `PatchAsync` pipeline per controller |
| S8 | **Create an item** | issue create | |
| F1 | **User-data fan-outs** — export slice, erasure, merge fold | the key service | Owed because `backdoor_api_keys` is user-keyed |

What follows from the table drives everything below:

- **S1 + S2 + F1 is the whole of Backdoor's own logic.** Everything under S3–S8 is translation:
  parse, delegate to another section's contracts interface, shape JSON. A rule that lives in a
  controller here is a rule in the wrong section.
- **S7 is one near-identical body per patch endpoint, spread across the controllers that
  carry it.** The right shape is one expression of the patch pipeline per controller.

## 3. Structure

The layout those shapes imply, written fresh:

```
Contracts/          one constant the Shell reads: the auth scheme name
Filters/            one authorization filter — S2
Services/           the key service + its result/row records, the audit discriminators — S1, F1
Data/               one repository over one table, one context, one config, one migration
Domain/             one entity
Models/             one view model for the one page
Controllers/        BackdoorController          — S1, HTML, Admin cookie
                    BackdoorLogsController      — S3 over Base's log sink
                    BackdoorAgentController     — S3, S4, S5
                    BackdoorSurveysController   — S3, S4, S5
                    BackdoorIssuesController    — S3, S4, S5, S6, S7, S8
                    BackdoorFeedbackController  — S3, S4, S5, S6, S7
Views/Backdoor/     one page
```

The structural rules the layout has to keep:

- **The project references exactly the assemblies its types come from.** Backdoor is a leaf that
  reaches the sections it serves; every one of those references is load-bearing or it is not
  there. A
  reference retained "because the section is served here" is dead weight that widens the graph.
- **A request-shaping default that can never fire is not a safety net.** S2 guarantees a
  principal before any S3–S8 body runs; a controller that also carries a fallback for its
  absence is describing a state the filter forbids.

## 4. Invariants

Stated so a violation is recognisable:

- A presented key resolves to exactly one person, and that person becomes the request principal —
  id plus active roles. No key, unknown key, revoked key → 401, all indistinguishable.
- A key reads no further than its holder does in the browser: the served queues are scoped by the
  owner's own id, roles and admin flag.
- The database never holds a plaintext key: SHA-256 hash plus a 12-character display prefix.
- A key authenticates only while its owner is **both** in Admin or Board **and** in
  `UserState.Active` — tested at issue, at rotate, and on every single request. Failing the test
  refuses the key; it never revokes it.
- Issue and revoke each write one audit entry naming the key and its owner. A rotate is a revoke
  entry followed by an issue entry.
- Every `/api/backdoor/*` write passes the key owner as the acting user. Nothing here writes as
  nobody.
- No controller in this section touches a repository or `DbContext` other than
  `IBackdoorApiKeyRepository`; every served datum arrives through another section's published
  contracts interface.
- A key-authed principal carries the Backdoor scheme and no state claims, and the
  Shell's onboarding gates let it through rather than redirecting a JSON client to HTML.
- Erasure hard-deletes the person's own keys and detaches them from anyone else's as both
  creator and revoker. Merge re-points every one of those columns onto the survivor.

## 5. Seams

Specified-but-unbuilt work. Not built here, not ranked — reserved so items touching it are
shaped by it.

- **Per-key scope.** A key is all-or-nothing across every surface it opens today. Nothing in the model
  says a key could be read-only or single-surface, and nothing has asked for it.

## 6. Deliberately not done

- **No caching decorator on the key service.** A cache would have to be invalidated on every
  revoke to stay correct about the one thing that matters most, and lookups are a single indexed
  hash probe at a handful of requests per minute.
- **No resource set.** Every string on the one page is English admin plumbing, read only by full
  Admins — the same call Debug makes.
- **No shared base controller for the machine surface.** A controller per served section, over
  shapes they share, looks like a base class; it would be one, in Base, serving one section, and would put the patch
  pipeline further from the rules it enforces. Collapse within a controller, not across them.
- **No auto-revocation on ineligibility.** Refusal is reversible and a transient role gap must
  not destroy a credential.
- **No FK constraints on the user columns.** Cross-section Guids by rule.

## Load-bearing weirdness

Essential complexity and settled decisions, so later runs stop re-litigating them:

- **The section exists to hold routes that came from other sections.** `/api/backdoor/logs`
  was Debug's, `/agent` was Agent's, `/issues` was Issues', `/feedback` was Feedback's,
  `/surveys` was Surveys'. Consolidating them is what made one auth model possible; the
  controllers living away from the data they serve is the point, not drift.
- **`Humans.Backdoor` references the section assemblies it serves.** That is legal precisely
  because
  each is reached only through a public contracts interface and nothing references Backdoor
  back — the graph stays acyclic because this section is a leaf.
- **The account-state half of eligibility is not redundant with the role half.** Suspension
  moves `users.State` and deliberately leaves role assignments standing, so a role-only test
  would keep authenticating a suspended admin's key.
- **`CreatedByUserId` is nullable.** Not because issuing is optional, but so GDPR erasure can
  detach a deleted admin from a key that still belongs to someone else.
- **The migrations-history table is `__EFMigrationsHistory_Backdoor`.** One database, one
  connection; the split is a code-side partition of the EF model.

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| section-doctor | 2026-09-03 | First doctoring: unclamped `?limit=` reaching SQL, a dead project reference, and the untested feedback controller | peterdrier/Humans#1586 |
