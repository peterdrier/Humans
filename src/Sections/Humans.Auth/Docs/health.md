<!-- freshness:triggers
  src/Sections/Humans.Auth/**
  src/Sections/Humans.Auth/Contracts/**
  src/Humans.Web/Authorization/SystemRoleClaimsTransformation.cs
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
  date; later they stop being one. The record of that is kept, so the association can
  answer "who was on the Board in March" as well as "who is on it now". Nothing in the section's
  own paths deletes a row — the one exception is the account merge, which drops a source row
  whose role the surviving account already holds actively, so that lifetime and its creator are
  lost (`SystemRoleRepository.ReassignToUserAsync`). Every change is
  attributed to whoever made it. A single admin assign or revoke also notifies the affected person
  and can carry a written reason; the bulk revoke on the deletion path deliberately does
  neither (§4, §6).
- **It lets a person prove an email address is theirs and get signed in.** They type an email;
  a short-lived link arrives; clicking it signs them in, or starts a signup if the address is
  new to us. It never reveals whether the address is known. Both link types are single-use:
  each is consumed at the moment it is redeemed — the login link on verification, the signup
  link on the form's POST, after validation so a blank field cannot burn it. A replayed signup
  POST is refused, except from the person who just signed up, who is already signed in and is
  simply sent onward. A signup that fails to create the account hands the link back rather than
  burning it. Survey and unsubscribe links are a different mechanism and stay reusable
  by design (F41, answered by Peter 2026-09-10).

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
| S2 | *Who holds role X?* | `GetActiveUserIdsInRoleAsync`, `GetUserIdsWithActiveAssignmentsAsync`; internal `GetActiveCountsByRoleAsync` (the metrics gauge only) | Projections of one active-set scan |
| S3 | *What does this person hold?* | `GetActiveForUserAsync`, `GetByUserIdAsync` | Active-only vs. full history |
| S4 | *Render the admin list / one row* | `GetFilteredAsync`, `GetByIdAsync` | The only paged, display-stitched shape |
| S5 | *Change who holds what* | `AssignRoleAsync`, `RevokeRoleAsync`, `RevokeAllActiveAsync` | The invariant-bearing shape; the overlap check is the repository's, asked only from inside `AssignRoleAsync` |
| S6 | *Flush a cache on my behalf* | — (gone 2026-10-21, HUM0028) | Was `InvalidateClaimsCacheForUser` and `InvalidateRoleAssignmentCache` — see §3 |
| S7 | *Sign this person in from an emailed link* | `SendSignInLinkAsync`, `VerifySignInTokenAsync`, `VerifySignupToken`, `ConsumeSignupTokenAsync`, `ReleaseSignupToken` | `ISignInLinkService`; the signup members split read (form prefill), redemption, and un-redemption when provisioning fails |
| S8 | *Stop this request unless it is a full Admin* | `RequireCurrentUserIsAdminAsync` | `IAdminAuthorizationService` |
| S9 | *May this principal manage this role name?* | `SystemRoleOperationRequirement` + its handler | ASP.NET resource-based, Shell-invoked |
| S10 | *Who is the current user?* | `ICurrentUserContext.UserId` | Declared here, implemented in Shell |

S1–S4 are `ISystemRoleService`; S5 is `ISystemRoleWrite`; the internal
`ISystemRoleManagement` joins both and adds only what the section itself asks for.
Implemented-but-uncontracted shapes ride along on the inner service: GDPR export/erasure
(`IUserDataContributor`) and account-merge re-FK (`IUserMerge`). Both are crosscut interfaces
the section implements rather than surface it publishes, and that is the right place for them.

## 3. Structure

Written fresh from the shapes above, not from today's tree.

- **The repository is the section's only SQL surface.** `auth_role_assignments` is the section's only
  data. The repository exposes exactly the queries the shapes need — no method exists for a
  caller that does not exist.
- **The service holds the invariants.** Temporal overlap, the ended/not-yet-active guard, the
  audit row, the notification, the cache pokes. Nothing else may write, and
  no member exists on the service for a question only its own write path asks.
- **The caching decorator wraps the service interface**, holding the whole row set in memory and
  deriving S1–S3 from it at the caller's clock instant. It is a decorator, so it may reach the
  inner service and never the repository; the row set it holds is the *only* thing it needs, so
  the warm path should ask for rows and nothing else — not for a display-stitched admin page it
  discards.
- **Sign-in has no persisted state.** `SignInLinkService` owns no table. Token minting and URL
  shape sit behind one collaborator; replay and cooldown state behind another. It names no
  `DbContext`, no `IDataProtectionProvider`, no `IMemoryCache`, and since 2026-10-20 it writes
  nothing: the `users.SignInLinkSentAt` stamp that used to persist the cooldown through
  `UserManager.UpdateAsync` is gone, column included (AUTH-2, Peter chose to drop restart
  survival). `UserManager<User>` is gone from the section too; lookups go through `IUserService`.
- **The section ships no controller and no view.** Its only resx is `AuthResource`, the copy of
  the two sign-in emails it sends. `AccountController` and
  `src/Humans.Web/Views/Account/*` stay in Shell because the controller is the app's sign-in surface: it drives
  sign-in (through Users' `IUserSignInService`) and the cookie and external-auth handlers the Shell
  configures in `Program.cs`, and the actions that do more than render or challenge provision
  users through `Humans.Users`. None of them writes `auth_role_assignments`. That is a deliberate
  boundary, not an unfinished move.
- **S6 does not belong on a system-role contract — and is gone.** The cache-flush verbs
  sat on `ISystemRoleWrite` so Users' `AccountMergeService` could poke Auth's caches after
  a merge fold. Since 2026-10-21 (HUM0028) the merge tells Auth *a merge happened* through
  `IUserMerge.ReassignAsync` and Auth clears its own row cache there; the per-user claims cache
  the other verb flushed was dropped, so both members left the contract.

## 4. Invariants

Stated so a violation is recognisable, each with the line that enforces it. These restate
`Auth.md`'s list in the form the run checks against.

1. An assignment is never resurrected. Revoking a role stamps `ValidTo`; granting it again writes
   a new row (`Services/SystemRoleService.cs:122`, `Services/SystemRoleService.cs:185`).
2. `AssignRoleAsync` refuses when an active or open-ended assignment for the same
   (user, role) overlaps the new window (`Services/SystemRoleService.cs:105`).
3. `RevokeRoleAsync` refuses a row that is already revoked or not yet active
   (`Services/SystemRoleService.cs:172`).
4. One *semantics* of "active at *t*" — `ValidFrom <= t && (ValidTo is null || ValidTo > t)` —
   governs every read, in SQL and in memory alike. It is necessarily implemented more than
   once (`Domain/RoleAssignment.cs:38`, `Services/SystemRoleRow.cs`,
   `Contracts/ISystemRoleService.cs`, and the repository's EF expressions, which cannot
   call a method and be translated). What this forbids is a second *predicate*, not a second
   implementation: an in-memory read that spells the comparison out where a method is in reach
   is the violation.
5. Assign, revoke, the bulk revoke and the merge fold (`ReassignAsync`) clear the row cache right
   after their repository write (`Services/SystemRoleService.cs`). There is no claims cache to flush: the Shell's
   claims transformation derives claims per request from the row cache.
6. Every single-row write leaves an audit entry attributed to the actor
   (`Services/SystemRoleService.cs:125`, `Services/SystemRoleService.cs:188`), and
   best-effort notifies the affected person; a notification failure never fails the write
   (`Services/SystemRoleService.cs:145`, `Services/SystemRoleService.cs:208`).
7. A role write calls out to no other section. Governance asks Google to sync the Board's group
   and files after a Board change it makes; the hourly group sweep catches the rest.
8. Only an Admin may assign `Admin`; Board and HumanAdmin are confined to
   `RoleNames.BoardManageableRoles` (`Authorization/SystemRoleAuthorizationHandler.cs:22`,
   `Authorization/SystemRoleAuthorizationHandler.cs:30`).
9. A sign-in-link token, login or signup, redeems once; a second presentation of the same token
   fails (`Services/SignInLinkService.cs:71`, `Services/SignInLinkService.cs:99`).
10. A sign-in-link send never discloses whether the address is known — an unknown address falls
    through to a signup send, and every rate-limited branch returns silently
    (`Services/SignInLinkService.cs:46`).
11. Login sends are capped at one per user per cooldown, reserved before the message is built
    and released only when building or sending fails (`Services/SignInLinkService.cs:131`,
    `Services/SignInLinkService.cs:149`). Signup sends are capped at one per inbox per cooldown,
    released on a failed send (`Services/SignInLinkService.cs:162`,
    `Services/SignInLinkService.cs:178`).
12. Verified emails only: an unverified address never resolves to a user on the sign-in path
    (`Services/SignInLinkService.cs:34`).

## 5. Seams — specified but not built

Reserved, not ranked, not built:

- **Cached S1/S2 derivations.** The decorator holds every row but still passes `HasActiveRoleAsync`
  and friends through to SQL. The cached row shape can already answer them; the migration was
  left "incremental as new callers arrive". Doing it is behaviour-preserving but changes the
  read path for authorization checks, so it wants its own PR and its own tests.
- **`RoleGroups` deletion.** `src/Humans.Base/Constants/RoleGroups.cs` has zero call sites and
  is slated for removal. Off-section (Base), so it belongs in a sweep, not here.

## 6. Deliberately not done

- **No FK constraint and no navigation property from `auth_role_assignments` to `users`.** The
  columns are bare `Guid`s on purpose — a nav would couple Auth's schema to Users' schema and
  is the coupling the architecture exists to prevent. Display names are stitched in memory.
- **No per-row cache invalidation.** Writes are rare; a wholesale flush is cheaper to reason
  about than a correct partial one.
- **No controller or view in the section.** Moving `AccountController` here would drag Users'
  and Profiles' writes with it.
- **No `OnboardingResult` reuse.** The section owns `SystemRoleResult` because the
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
  resolves it in their scope, instead of each getting a private `SystemRoleService`, and
  neither re-enters the decorator. It is not redundant registration. Note the decorator itself
  is a Singleton that opens a fresh scope per call, so it never shares an instance with a
  caller's scope.
- **A Singleton repository over `IDbContextFactory`.** Deliberate: the repository owns each
  context's lifetime while `AuthDbContext` stays Scoped.
- **`FindForMutationAsync` reads `AsNoTracking`.** Correct here — `UpdateAsync` re-attaches to
  a *fresh* context, so tracking in the read context would buy nothing. That leaves its body
  identical to `GetByIdAsync`'s; the two names say which caller mutates, so they stay two.
- **`SignInLinkService` releases the cooldown reservation on a failed send.** A failed send
  must not consume the user's cooldown. The login cooldown is the in-memory reservation alone;
  it closes the overlapping-request race and is deliberately not persisted.
- **Auth is a horizontal that references vertical *leaves*.** Legal since Peter's Base-floor
  decision of 2026-08-14. Inverting `SystemRoleService`'s reads of Users and Teams remains a
  Peter-led item, not a refactor to do in passing; ordinary fixes in the class are fine.

## Run history

| Run | Date | Headline | PR |
|-----|------|----------|-----|
| 1 | 2026-09-01 | First pass — dead repository surface, doc drift, mock-shaped cache tests | [peterdrier/Humans#1575](https://github.com/peterdrier/Humans/pull/1575) |
| 2 | 2026-10-19 | Dead overlap member gone, re-grant and boundary invariants pinned, docs and comments say what is true now | [peterdrier/Humans#2035](https://github.com/peterdrier/Humans/pull/2035) |
