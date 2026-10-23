<!-- freshness:triggers
  src/Sections/Humans.Gdpr/**
  src/Sections/Humans.Gdpr/Contracts/**
-->
<!-- freshness:flag-on-change
  The fan-out contract (IUserDataContributor / UserDataSlice) and the failure rules the orchestrator enforces — never swallow a contributor exception, never accept a duplicate export section name, log an error for an exported section with no erasure declaration, never leave an erasure section behind silently — review whenever the Contracts/ folder or GdprService changes.
-->

# Gdpr — Section Invariants

## Concepts

- **Gdpr** is the GDPR subject-rights orchestrator — both the **Article 15**
  export and the **Article 17** erasure. It owns no database tables and no
  repository. Its entire substance is two fan-outs over the same contributor
  roster: ask every section that holds personal data for its slice and merge
  the slices into one document (export), or run every section's erasure in turn
  (erasure). Its controllers host the member-facing subject-rights surface — the
  data-export downloads, the deletion request and the pending-deletion and
  account-status landing pages — and compute nothing: the export is `IGdprService`,
  the deletion work is Users' `IAccountDeletionService`.
- **`IUserDataContributor`** is the fan-out contract (an `IFanout`, per
  `memory/architecture/orchestrator-marker.md`). Every service that owns
  user-scoped tables implements it and returns the personal data it — and only
  it — owns.
- **`UserDataSlice`** is one contributor's answer: a section name the
  contributor chooses, plus a payload. Each contributor declares its own
  section-name constants — there is no central registry. The export format is
  not a spec: it changes as contributors change, and nothing outside this
  codebase reads it.
- **`GdprExport`** is the envelope — an ISO-8601 UTC timestamp, the user id (the
  surviving account when asked under a merged-away id, else the id asked with),
  the merged-away ids folded into it, and the merged section bag. Each download
  route uses `GdprExportSerializer` in Contracts to serialize it to the file.
- **Erasure shares the contract *and* the orchestrator.** `IUserDataContributor`
  also carries `ErasureDeclaration` (a static section-name →
  retention-reason table; `null` = erased in full) and `EraseForUserAsync`, so a
  section cannot export a category without accounting for its deletion — the
  orchestrator checks this at export time. Both
  loops live here: `IGdprService.EraseForUserAsync` runs the erasure fan-out
  (every contributor for one id) beside the export one. The **deletion
  lifecycle** — grace period, ticket hold, audit entries, confirmation email,
  merge-chain walk and cache invalidation — stays under Users
  (`AccountDeletionService`, driven by `ProcessAccountDeletionsJob`); its own
  doc owns the detail.

## Routing

Controllers and views (`Views/User/Status`, `Views/User/Deletion`), so the project is
`Microsoft.NET.Sdk.Razor`. Every route here is under `/Gdpr/*`; the account-deletion confirmation
email links `/Gdpr/Deletion`.

| Route | Controller action | Notes |
|-------|------------------|-------|
| `GET /Gdpr/DownloadData` | `MemberDataController.DownloadData` | For a human who has completed onboarding; own data only |
| `POST /Gdpr/RequestDeletion` | `MemberDataController.RequestDeletion` | Starts the deletion countdown via `IAccountDeletionService.RequestDeletionAsync`; redirects back to `/Consent/MyData` |
| `GET /Gdpr/Guest/DownloadData` | `GuestDataController` | For an authenticated account with no profile yet |
| `POST /Gdpr/Guest/RequestDeletion` | `GuestDataController.RequestDeletion` | Profileless deletion request via `IAccountDeletionService.RequestDeletionAsync` (ticket hold shown in the flash); redirects to `/Guest` |
| `POST /Gdpr/Guest/CancelDeletion` | `GuestDataController.CancelDeletion` | `IAccountDeletionService.CancelDeletionAsync`; redirects to `/Guest` |
| `GET /Gdpr/Status` | `UserController.Status` | Account-status wall for Suspended/AdminSuspended/Rejected/Deleted/Merged |
| `GET /Gdpr/Deletion` | `UserController.Deletion` | Pending-deletion landing; redirects home when nothing is pending |
| `POST /Gdpr/Deletion/Cancel` | `UserController.CancelDeletion` | `IAccountDeletionService.CancelDeletionAsync` |

The views' copy lives in `GdprResource` (six cultures; keys `Gdpr_AccountStatus_*`, `Gdpr_AccountDeletion_*`, `Gdpr_Deletion_*`, and the guest deletion flashes `Gdpr_Guest_*`). For a Rejected account the wall renders the `UserPartSlots.AccountStatus` slot (Base), where Consent contributes the rejection reason — Gdpr cannot reference Consent, which owns the record. Users keeps its own copies of the suspension keys its notification text reads.

The `/Consent/MyData` page itself is Consent's (`MyDataController`) and links to the two
`MemberData` actions. The `User` controller and the two `MemberData` actions are exempt from
`MembershipRequiredFilter` — they are its redirect targets and the recovery actions a non-Active
account keeps (terminal states are denied everything but `Status`).

The export actions resolve `IGdprService` from the `Contracts/` folder and use its shared
`GdprExportSerializer` for the file download. The serializer writes the envelope
fields first, then section slices in contributor order, as indented UTF-8 JSON
with string enums and null object properties omitted. Erasure has no route of its own — `AccountDeletionService`
calls `EraseForUserAsync` per merge-chain id from the deletion paths.

## Actors & Roles

| Actor | Capabilities |
|-------|-------------|
| Any authenticated human | Downloads **their own** export, and only their own — both export actions resolve the caller's own user id and never take one from the request |
| Admin / Board | Nothing extra. There is no admin route that exports someone else's data |

## Invariants

- **Export resolves the subject before fan-out.** Every contributor receives the
  surviving account ID, matching the envelope, so moved rows remain visible when
  the request names an archived account. Contributors with retained history still
  read the survivor's merge-chain IDs. An unknown account keeps the requested ID.
  Erasure remains a separate per-ID fan-out; its input is never redirected.
- **Every contributor gets a second, chain-aware call.** Right after
  `ContributeForUserAsync`, the orchestrator always calls
  `ContributeForIdentityChainAsync(survivor, allUserIds)` — the survivor first,
  then every archived id merged into it (`UserInfo.AllUserIds`). The default
  returns nothing; a contributor that cannot resolve the chain itself (Email, a
  crosscut barred from Users) overrides it. Its slices join the same section bag
  under the same rules — one non-null section name from both methods is a
  duplicate and fails the export (a null slice is dropped before the check).
- **The export is complete or it fails.** A contributor that throws is logged
  and the exception is re-thrown: omitting a category silently is worse than
  failing the download. Erasure holds the same rule — a contributor that throws
  aborts `EraseForUserAsync`, so the caller leaves its deletion markers set and
  the whole cascade retries the next day rather than leaving data behind.
- A request-aborted guest export propagates cancellation rather than redirecting
  with a failed-download message; other export failures retain that message.
- Caller cancellation during export contributor work is logged at Warning without
  a stack trace and re-thrown. Contributor cancellation without an aborted caller
  remains an Error with its exception, like other contributor faults.
- **Erasure runs the identity collapse last, and takes only an id.**
  The contributor whose `ErasesLast` is `true` erases last, so sections
  that still need the human's addresses to reach an external processor (the
  Workspace suspend) can resolve them; ordering is derived from each
  contributor's own declaration, not a pinned type list. `EraseForUserAsync` takes a single id — Users loops it
  over the merge chain (archived ids first, survivor last) and invalidates each
  id as it completes — so Gdpr keeps no dependency on the Users merge primitive
  or its caches, and a mid-chain failure still leaves the already-erased ids'
  caches dropped.
- **Non-null section names are unique across contributors and both calls.** A
  duplicate among non-null slices is a programming error and throws `InvalidOperationException` naming the section —
  it is not last-writer-wins.
- **A `null` slice is dropped; an empty collection is not.** `Data is null`
  means the entity does not exist for this user (a profileless account has no
  `Profile`) and the key is omitted. A collection section with no rows must
  return an empty list, which survives into the JSON as `[]` rather than being
  omitted.
- **The fan-out is sequential, never `Task.WhenAll`.** A simplicity choice, not
  a correctness requirement: one contributor at a time keeps failure
  attribution and log order plain, and there is nothing to win at this scale
  (`design-rules.md` §8b).
- **No cross-section database reads.** A contributor reads only its own
  section's tables; data from another section arrives through that section's
  own contributor, never through an `Include` chain.
- **The orchestrator owns no tables and injects no repository or `DbContext`**
  ([`no-tests-for-absences`](../../../../memory/architecture/no-tests-for-absences.md): documentation, not a pinned assertion).

## Negative Access Rules

- No endpoint exports another human's data. No controller action accepts a
  user id.
- Gdpr must not acquire a repository, a `DbContext` or a table. The moment it
  reads a table directly it is duplicating a contributor and the "no
  cross-section reads" invariant is gone.
- Gdpr must not name another section's concrete service. It knows contributors
  only as `IEnumerable<IUserDataContributor>`; who is in that list is each
  section's own DI registration.

## Triggers

- `ExportForUserAsync` writes no data and raises no notification. It logs one
  informational line per export (`user … exported their data (N sections)`) and
  one error line per contributor fault; caller cancellation during contributor
  work writes a warning without its exception.
- `EraseForUserAsync` writes through the contributors (each erases its own
  section) and raises no notification itself — the audit entry and confirmation
  email belong to the Users deletion lifecycle. It logs one error line per
  contributor failure before re-throwing.
- Adding a user-scoped section: declare its section-name constants on its own
  contributor class, implement `IUserDataContributor` on its owning service,
  and register the forwarding factory beside that service — the DI
  architecture test discovers the new contributor by reflection, no list to
  update. The new constants must also appear in that contributor's
  `ErasureDeclaration` or `GdprService.ExportForUserAsync` logs an error and
  continues at export time.

## Cross-Section Dependencies

**Outbound:** `Humans.Gdpr` references `Humans.Users.Contracts`. `GdprService`
injects `IUserService` and calls `GetUserInfoAsync` once per export to
stamp the envelope with the surviving account id and the merged-away ids,
and pass that surviving id (and, to the chain-aware call, the whole chain) to every
export contributor;
the controllers' base class `HumansControllerBase` also takes
`IUserService`, and `GuestDataController`, `MemberDataController` and `UserController` inject Users.Contracts'
`IAccountDeletionService` (the deletion work stays in Users; the deletion timestamps stay on
`users` behind `IUserWrite`); the download controllers (`GuestDataController`,
`MemberDataController`) also inject `IGdprService`.

**Inbound:** wide, and all of it through the public `Contracts/` folder. Every section owning
user-scoped tables references `Humans.Gdpr` to implement
`IUserDataContributor`; Users also references it for `AccountDeletionService` (the erasure caller).
`Humans.Web` references Gdpr only to compose sections.

## Architecture

**Owning services:** `GdprService` (`Humans.Gdpr.Services`, `internal
sealed`) behind the public `IGdprService` in `Contracts/`, exposing both
`ExportForUserAsync` and `EraseForUserAsync`.
**Owned tables:** none.
- `src/Sections/Humans.Gdpr` — one internal service, the internal controllers
  (`GuestDataController`, `MemberDataController`, `UserController`), the views
  (`Views/User/Status`, `Views/User/Deletion`) rendering over Base's shared view
  layer, `Section.cs`, this doc. No `Data/`, no migrations, no
  `Humans.Infrastructure` reference and no `Humans.UI` reference: nothing to
  persist.
- `src/Sections/Humans.Gdpr/Contracts` — the whole outward surface (namespace
  `Humans.Gdpr.Contracts`). Every section owning user-scoped tables implements
  `IUserDataContributor` from it, so each references `Humans.Gdpr`; the
  orchestrator and controllers stay `internal`.
- `GdprResource` (section root, six cultures) holds the section's page copy and
  flash messages; its controllers take `IStringLocalizer<GdprResource>`.
- The contributor forwarding factories deliberately do **not** live in
  `Section.Register`: each
  `AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<X>())` belongs to
  the section that owns `X` and is registered beside it.
- **Decorator decision:** no caching decorator. An export is a one-off download
  assembled from live data; caching it would be a privacy hazard, not a
  performance win.
- **Known gap:** the contributor-coverage tests are reflection-based, so a new
  user-scoped section whose owning service never implements
  `IUserDataContributor` at all leaves nothing to enumerate and the suite
  passes vacuously. The guardrail is prose in `design-rules.md` §8a.
