<!-- freshness:triggers
  src/Sections/Humans.Auth/**
  src/Sections/Humans.Auth.Contracts/**
  src/Humans.Web/Authorization/RoleAssignmentClaimsTransformation.cs
  src/Humans.Web/Controllers/AccountController.cs
-->

# Auth — Target Shape

The shape this section is converging on, re-derived each `section-doctor` run before any
tool runs. Invariants and the shipped surface live in [`Auth.md`](Auth.md); this file is the
design the run measures that against. Run history is at the bottom.

## 1. What the section does

Both jobs below are about *who you are and what you may do*, neither about who you are as a
member.

- **It remembers which offices a person holds, and when.** Someone is made a Board member on a
  date; later they stop being one. The record of that is kept forever, so the association can
  answer "who was on the Board in March" as well as "who is on it now". Every change is
  attributed to whoever made it. A single admin assign or end also notifies the affected person
  and can carry a written reason; the bulk revoke on the deletion path deliberately does
  neither (§4, §6).
- **It lets a person prove an email address is theirs and get signed in.** They type an email;
  a short-lived link arrives; clicking it signs them in, or starts a signup if the address is
  new to us. It never reveals whether the address is known. A *login* link is single-use — it
  is consumed on redemption. A *signup* link is not, and nothing consumes it: it is verified
  again on the form's POST, and a POST replayed inside its 15-minute window signs the holder
  into the account that the first POST created. Whether that asymmetry is intended is F41,
  open for Peter — describe it, do not "fix" it.

Everything else the section holds exists to serve one of those two: caches so the first job
can be asked on every request without a query, and rate limits so the second cannot be used
to spam an inbox or guess who has an account.

## 2. The shapes

The external surface, grouped by the question a caller is asking. Method counts matter less
than shape counts: a shape with five methods is a candidate for collapse; a method that is its
own shape is a candidate for removal.

| # | Question a caller is asking | Members that answer it | Note |
|---|---|---|---|
| S1 | *Does this person hold role X right now?* | `IsUserAdminAsync`, `IsUserBoardMemberAsync`, `IsUserTeamsAdminAsync`, `HasActiveRoleAsync`, `HasAnyActiveAssignmentAsync` | All but `HasAnyActiveAssignmentAsync` are `HasActiveRoleAsync` with a constant baked in |
| S2 | *Who holds role X?* | `GetActiveUserIdsInRoleAsync`, `GetUserIdsWithActiveAssignmentsAsync`, `GetActiveCountsByRoleAsync` | Projections of one active-set scan |
| S3 | *What does this person hold?* | `GetActiveForUserAsync`, `GetByUserIdAsync` | Active-only vs. full history |
| S4 | *Render the admin list / one row* | `GetFilteredAsync`, `GetByIdAsync` | The only paged, display-stitched shape |
| S5 | *Change who holds what* | `AssignRoleAsync`, `EndRoleAsync`, `RevokeAllActiveAsync`, `HasOverlappingAssignmentAsync` | The invariant-bearing shape |
| S6 | *Flush a cache on my behalf* | `InvalidateClaimsCacheForUser`, `InvalidateNavBadgeCache`, `InvalidateRoleAssignmentCache` | Not a question about roles at all — see §3 |
| S7 | *Sign this person in from an emailed link* | `SendMagicLinkAsync`, `VerifyLoginTokenAsync`, `VerifySignupToken`, `FindUserByVerifiedEmailAsync` | `IMagicLinkService` |
| S8 | *Stop this request unless it is a full Admin* | `RequireCurrentUserIsAdminAsync` | `IAdminAuthorizationService` |
| S9 | *May this principal manage this role name?* | `RoleAssignmentOperationRequirement` + its handler | ASP.NET resource-based, Shell-invoked |
| S10 | *Who is the current user?* | `ICurrentUserContext.UserId` | Declared here, implemented in Shell |

Implemented-but-uncontracted shapes ride along on the inner service: GDPR export/erasure
(`IUserDataContributor`) and account-merge re-FK (`IUserMerge`). Both are crosscut interfaces
the section implements rather than surface it publishes, and that is the right place for them.

## 3. Structure

Written fresh from the shapes above, not from today's tree.

- **One table, one repository.** `role_assignments` is the section's only data. The repository
  is the sole SQL surface and exposes exactly the queries the shapes need — no method exists
  for a caller that does not exist.
- **One service holds the invariants.** Temporal overlap, the ended/not-yet-active guard, the
  audit row, the notification, the Board team sync, the cache pokes. Nothing else may write.
- **One caching decorator over the service interface**, holding the whole row set in memory and
  deriving S1–S3 from it at the caller's clock instant. It is a decorator, so it may reach the
  inner service and never the repository; the row set it holds is the *only* thing it needs, so
  the warm path should ask for rows and nothing else — not for a display-stitched admin page it
  discards.
- **Sign-in is nearly an orchestrator.** `MagicLinkService` owns no table. Token minting and URL
  shape sit behind one collaborator; replay and cooldown state behind another. It names no
  `DbContext`, no `IDataProtectionProvider`, no `IMemoryCache`. One thing disqualifies it today:
  `SendLoginLinkAsync` stamps `user.MagicLinkSentAt` and persists it with `UserManager.UpdateAsync`
  — a write into Users' table from Auth, not through a Users interface. That is debt (F42), not
  the target: the cooldown belongs behind a Users write method or in `IMagicLinkRateLimiter`
  alongside the signup reservation it already owns.
- **The section ships no controller, no view, no resx.** `AccountController` and
  `Views/Account/*` stay in Shell because every action they expose writes another section's
  tables. That is a deliberate boundary, not an unfinished move.
- **S6 does not belong on a role-assignment contract.** The cache-flush verbs sit on
  `IRoleAssignmentService` so that one caller — Users' `AccountMergeService` — can poke
  Auth's caches after a merge fold commits. The target shape is that a merge tells Auth *a
  merge happened* (the `IUserMerge` seam it already implements) and Auth decides what to
  invalidate. Recorded as a seam (§5), not struck: it is a public-surface change.

## 4. Invariants

Stated so a violation is recognisable. These restate `Auth.md`'s list in the form the run
checks against.

1. An assignment is never resurrected. Ending a role stamps `ValidTo`; granting it again writes
   a new row. `ValidTo` is never cleared.
2. `AssignRoleAsync` refuses when an active or open-ended assignment for the same
   (user, role) overlaps the new window.
3. `EndRoleAsync` refuses a row that is already ended or not yet active.
4. One *semantics* of "active at *t*" — `ValidFrom <= t && (ValidTo is null || ValidTo > t)` —
   governs every read, in SQL and in memory alike. It is necessarily implemented more than
   once (`RoleAssignment.IsActive`, `RoleAssignmentRow.IsActiveAt`,
   `RoleAssignmentSummarySnapshot.IsActive`, and the repository's EF expressions, which cannot
   call a method and be translated). What this forbids is a second *predicate*, not a second
   implementation: an in-memory read that spells the comparison out where a method is in reach
   is the violation (F29).
5. Assign, end, and the bulk revoke invalidate the row cache and the claims cache for the
   affected user before the call returns. The merge path (`ReassignAsync`) is the exception by
   contract: it leaves invalidation to `AccountMergeService.MergeAsync`, which flushes after the
   merge's commit point.
6. Every single-row write leaves an audit entry attributed to the actor, and best-effort
   notifies the affected person; a notification failure never fails the write.
7. Granting or ending `Board` reconciles the Board system team.
8. Only an Admin may assign `Admin`; Board and HumanAdmin are confined to
   `RoleNames.BoardManageableRoles`.
9. A magic-link login token verifies once. A second presentation of the same token fails.
10. A magic-link send never discloses whether the address is known — the caller sees one
    outcome whether a login link, a signup link, or nothing at all was sent.
11. Signup sends are capped at one per address per cooldown, and the reservation is released
    when the send itself fails so a real user can retry.
12. Verified emails only: an unverified address never resolves to a user on the sign-in path.

## 5. Seams — specified but not built

Reserved, not ranked, not built:

- **S6's inversion.** Cache invalidation flowing from the merge seam rather than three public
  flush verbs. Public-surface change; needs Peter.
- **Cached S1/S2 derivations.** The decorator holds every row but still passes `HasActiveRoleAsync`
  and friends through to SQL. The cached row shape can already answer them; the migration was
  left "incremental as new callers arrive". Doing it is behaviour-preserving but changes the
  read path for authorization checks, so it wants its own PR and its own tests.
- **`RoleGroups` deletion.** `src/Humans.Base/Constants/RoleGroups.cs` has zero call sites and
  is slated for removal. Off-section (Base), so it belongs in a sweep, not here.

## 6. Deliberately not done

- **No FK constraint and no navigation property from `role_assignments` to `users`.** The
  columns are bare `Guid`s on purpose — a nav would couple Auth's schema to Users' schema and
  is the coupling the architecture exists to prevent. Display names are stitched in memory.
- **No per-row cache invalidation.** Writes are rare; a wholesale flush is cheaper to reason
  about than a correct partial one.
- **No controller or view in the section.** Moving `AccountController` here would drag Users'
  and Profiles' writes with it.
- **No `OnboardingResult` reuse.** The section owns `RoleAssignmentResult` because the
  alternative is a horizontal referencing a vertical's vocabulary.
- **No architecture test asserting the reference list.** Peter's Base-floor decision deleted
  the premise; the reasons live as comments beside each `ProjectReference`.
- **No test asserting a type *lacks* a constructor parameter.** Forbidden by
  [`no-tests-for-absences`](../../../../memory/architecture/no-tests-for-absences.md); the
  "names no `DbContext`" properties are documentation, not pins.

## Load-bearing weirdness

Settled decisions that read as smells. Stop re-litigating these.

- **The keyed-inner DI dance in `Section.cs`.** The inner service is registered keyed, an
  unkeyed concrete forwards to it by cast, and `IUserDataContributor`/`IUserMerge` resolve
  through that concrete. What it buys is that all three aliases reach the *undecorated* inner
  through one registration — GDPR export and merge share an instance with whatever else
  resolves it in their scope, instead of each getting a private `RoleAssignmentService`, and
  neither re-enters the decorator. It is not redundant registration. Note the decorator itself
  is a Singleton that opens a fresh scope per call, so it never shares an instance with a
  caller's scope.
- **A Singleton repository over `IDbContextFactory`.** Deliberate: the repository owns each
  context's lifetime while `AuthDbContext` stays Scoped.
- **`FindForMutationAsync` reads `AsNoTracking`.** Correct here — `UpdateAsync` re-attaches to
  a *fresh* context, so tracking in the read context would buy nothing.
- **`IRoleAssignmentCacheInvalidator` carries `[Grandfathered]` HUM0028.** Known debt with an
  issue; not a pattern to copy and not today's cleanup.
- **`MagicLinkService` stamps `MagicLinkSentAt` after the send, not before.** A failed send
  must not consume the user's cooldown.
- **Auth is a horizontal that references vertical *leaves*.** Legal since Peter's Base-floor
  decision of 2026-08-14. The `[DontFix]` on `RoleAssignmentService` records the remaining
  inversion as a Peter-led item, not a bug to fix in passing.

## Run history

| Run | Date | Headline | PR |
|-----|------|----------|-----|
| 1 | 2026-09-01 | First pass — dead repository surface, doc drift, mock-shaped cache tests | [#1575](https://github.com/peterdrier/Humans/pull/1575) |
