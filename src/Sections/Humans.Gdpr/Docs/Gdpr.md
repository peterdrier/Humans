<!-- freshness:triggers
  src/Sections/Humans.Gdpr/**
  src/Sections/Humans.Gdpr.Contracts/**
-->
<!-- freshness:flag-on-change
  The fan-out contract (IUserDataContributor / UserDataSlice / GdprExportSections) and the two failure rules the orchestrator enforces — never swallow a contributor exception, never accept a duplicate section name — review whenever the leaf or GdprExportService changes.
-->

# Gdpr — Section Invariants

Written at the section's G5 move (nobodies-collective/Humans#866). The G0 audit
(`docs/plans/2026-08-03-g0-first-audit/Gdpr.md`) recorded predicate 7 —
"`docs/sections/Gdpr.md` exists" — as a flat **FAIL**; this file is that gap
closed, transcribed from the code rather than from memory.

## Concepts

- **Gdpr** is the GDPR **Article 15** export orchestrator. It owns no database
  tables and no repository. Its entire substance is a fan-out: ask every
  section that holds personal data for its slice, merge the slices into one
  document, hand it back. It gained one controller at
  nobodies-collective/Humans#1091 (`GuestDataController`) purely to host the
  `/Guest/DownloadData` route — the route's own byte-identical move, not a
  change to what the section computes.
- **`IUserDataContributor`** is the fan-out contract (an `IFanout`, per
  `memory/architecture/orchestrator-marker.md`). Every service that owns
  user-scoped tables implements it and returns the personal data it — and only
  it — owns. There are 21 implementers, all in already-moved G5 sections —
  `Humans.Application` was emptied and deleted at G5 lane B6
  (nobodies-collective/Humans#1347).
- **`UserDataSlice`** is one contributor's answer: a stable JSON section name
  plus a payload. **`GdprExportSections`** holds those names as constants, so
  the document's top-level keys survive a contributor moving between services.
- **`GdprExport`** is the envelope — an ISO-8601 UTC timestamp and the merged
  section bag, which is what the two controllers serialize to the download.
- **Erasure shares the contract, not the orchestrator.** Since
  nobodies-collective/Humans#853 `IUserDataContributor` also carries
  `ErasureDeclaration` (a static `GdprExportSections` → retention-reason table;
  `null` = erased in full) and `EraseForUserAsync`, so a section cannot export a
  category without accounting for its deletion. The orchestration still lives
  under **Users**: `IAccountDeletionService` / `AccountDeletionService`, driven
  by `ProcessAccountDeletionsJob`, fans the erasure out over
  `IEnumerable<IUserDataContributor>`. The 2026-08-03 frozen inventory assigns
  "export/erasure" to Gdpr and the code still splits them; that is an open
  ruling (G0 gap #2), unchanged here.

## Routing

One controller, no views — the project is still plain `Microsoft.NET.Sdk`
rather than `Microsoft.NET.Sdk.Razor` because `GuestDataController` only ever
returns `File()` or a redirect.

| Route | Controller action | Notes |
|-------|------------------|-------|
| `GET /Profile/Me/DownloadData` | `ProfileController` (Humans.Users) | For a human who has completed onboarding |
| `GET /Guest/DownloadData` | `GuestDataController` (Humans.Gdpr) | For an authenticated account with no profile yet — moved here at nobodies-collective/Humans#1091, route unchanged |

Both resolve `IGdprExportService` from the contracts leaf and serialize the
result to a file download. `/Profile/Me/DownloadData` stays on Users — moving
it would be a URL change, which is out of scope here too.

## Actors & Roles

| Actor | Capabilities |
|-------|-------------|
| Any authenticated human | Downloads **their own** export, and only their own — both actions resolve the caller's own user id and never take one from the request |
| Admin / Board | Nothing extra. There is no admin route that exports someone else's data |

## Invariants

- **The export is complete or it fails.** A contributor that throws is logged
  and the exception is re-thrown: omitting a category silently is worse than
  failing the download.
- **Section names are unique across contributors.** A duplicate is a
  programming error and throws `InvalidOperationException` naming the section —
  it is not last-writer-wins.
- **A `null` slice is dropped; an empty collection is not.** `Data is null`
  means the entity does not exist for this user (a profileless account has no
  `Profile`) and the key is omitted. A collection section with no rows must
  return an empty list, which survives into the JSON as `[]` — downstream
  comparison tools and support procedures depend on that stability.
- **The fan-out is sequential, never `Task.WhenAll`.** Contributors share the
  scoped `HumansDbContext`, which is not thread-safe.
- **No cross-section database reads.** A contributor reads only its own
  section's tables; data from another section arrives through that section's
  own contributor, never through an `Include` chain.
- **The orchestrator owns no tables and injects no repository or `DbContext`**
  ([`no-tests-for-absences`](../../../../memory/architecture/no-tests-for-absences.md): documentation, not a pinned assertion).

## Negative Access Rules

- No endpoint exports another human's data. Neither controller action accepts a
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
  one error line per contributor failure.
- Adding a user-scoped section: add its section-name constants to
  `GdprExportSections`, implement `IUserDataContributor` on its owning service,
  register the forwarding factory beside that service, and add the type to
  `GdprExportDependencyInjectionTests.ExpectedContributorTypes`. The new
  constants must also appear in that contributor's `ErasureDeclaration` or
  `GdprErasureCoverageTests` fails the build.

## Cross-Section Dependencies

**Outbound:** none at compile time. The section names no other section's type —
the fan-out is over its own interface.

**Inbound:** the widest of any moved section, and all of it through the leaf.
Every contributor implements `Humans.Gdpr.Contracts.IUserDataContributor`, so
20 section projects reference `Humans.Gdpr.Contracts`, plus `Humans.Web` for
the two download controllers. It references `Humans.Base` alone, so
none of that cycles.

## Architecture

**Owning services:** `GdprExportService` (`Humans.Gdpr.Services`, `internal
sealed`) behind the public `IGdprExportService` on the leaf.
**Owned tables:** none.
**Status:** (A) Migrated. Own project since G5 (nobodies-collective/Humans#866).

- `src/Sections/Humans.Gdpr` — one internal service, one `Section.cs`, this
  doc. No `Data/`, no migrations, no `Humans.Infrastructure` reference and no
  `Humans.UI` reference: nothing to persist and nothing to render.
- `src/Sections/Humans.Gdpr.Contracts` — the whole outward surface, and the
  reason the leaf is a *project* rather than a folder is stronger than the usual
  consumer-in-Base test: Base does not merely call this section, it
  **implements** its contract.
- No `Resources/` folder and no `GdprResource`: the section has no page copy at
  all, so `SectionResourceTypes()` returns one fewer marker, and no type here
  takes `IStringLocalizer<T>` for any `T` (Gate's strict form) — documentation,
  not a pinned assertion ([`no-tests-for-absences`](../../../../memory/architecture/no-tests-for-absences.md)).
- The contributor forwarding factories deliberately did **not** move into
  `Section.Register`: each
  `AddScoped<IUserDataContributor>(sp => sp.GetRequiredService<X>())` belongs to
  the section that owns `X` and is registered beside it.
- **Decorator decision:** no caching decorator. An export is a one-off download
  assembled from live data; caching it would be a privacy hazard, not a
  performance win.
- **Known gap (G0 G3 gap #1, unchanged):** the contributor-coverage tests are
  reflection-based, so a new user-scoped section whose owning service never
  implements `IUserDataContributor` at all leaves nothing to enumerate and the
  suite passes vacuously. The guardrail is prose in `design-rules.md` §8a.
