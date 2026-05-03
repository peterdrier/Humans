# PR 2 — Identity-property Overrides, Custom UserStore, Drop Identity Columns

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `UserEmails` the canonical source of truth for a human's email by (a) sweeping every remaining read of `User.Email` / `NormalizedEmail` / `UserName` / `NormalizedUserName` to UserEmails-backed queries, (b) overriding the four properties on `User` so getters compute from `UserEmails` and setters throw, (c) implementing a custom `HumansUserStore` that routes Identity's email-lookup contract through `IUserEmailService`, and (d) running an EF migration that drops the four columns + the `EmailIndex` / `UserNameIndex` indexes — preceded by an idempotent defensive `INSERT ... WHERE NOT EXISTS` that catches any orphan Users the PR 1 admin button missed. Also removes the orphan `/Contacts` admin surface (controller + service + views) and the now-no-op anonymization writes in `UserRepository`.

**Architecture:** PR 2 of the 6-PR email-identity-decoupling sequence. PR 1 stopped Identity-column writes and added the admin backfill button. PR 2 is the Identity-surgery slice — virtual property overrides, custom user store, column drop. After this PR, no production code path reads the four columns and the columns themselves are gone.

**Tech Stack:** ASP.NET Core 10, ASP.NET Identity, EF Core 10 + PostgreSQL, NodaTime (`Instant`), AwesomeAssertions, xUnit, NSubstitute, Mono.Cecil-based architecture tests (introduced in PR 1).

**Spec:** `docs/superpowers/specs/2026-04-27-email-and-oauth-decoupling-design.md` (PR 2 section, revised 2026-04-28).

**Issue:** nobodies-collective/Humans#601

**Risk:** **High.** Identity surgery — virtual property overrides + custom user store + column drop. Forward-only after deploy. The PR 1 admin button + this migration's defensive INSERT are the two layers that protect orphan Users from being locked out.

---

## File Map

### Modified
- `src/Humans.Domain/Entities/User.cs` — virtual overrides for `Email`, `NormalizedEmail`, `EmailConfirmed`, `NormalizedUserName`
- `src/Humans.Infrastructure/Data/Configurations/Users/UserConfiguration.cs` — `b.Ignore(...)` for the four overridden properties
- `src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs` — `.AddUserStore<HumansUserStore>()`
- `src/Humans.Application/Services/Auth/MagicLinkService.cs` — replace 4 read sites (`FindByEmailAsync` x2, `GetByNormalizedEmailAsync` x1, optionally remove `FindUserByAnyEmailAsync` method if no remaining callers)
- `src/Humans.Application/Services/Users/AccountProvisioningService.cs` — replace `GetByEmailOrAlternateAsync` step 2 with UserEmails lookup
- `src/Humans.Application/Services/GoogleIntegration/GoogleWorkspaceSyncService.cs` — replace `GetByEmailOrAlternateAsync` at line 306
- `src/Humans.Application/Services/GoogleIntegration/GoogleAdminService.cs` — replace `GetByEmailOrAlternateAsync` at line 176
- `src/Humans.Web/Controllers/DevLoginController.cs` — replace 2 `FindByEmailAsync` sites
- `src/Humans.Web/Infrastructure/DevelopmentDashboardSeeder.cs` — replace `u.Email LIKE` LINQ filter
- `src/Humans.Web/Controllers/AccountController.cs` — remove lockout-relink branch (lines 104-158) and `FindUserByAnyEmailAsync` branch (lines 207-242)
- `src/Humans.Application/Services/Profile/UserEmailService.cs` — sweep `user.Email` references at 121 / 471-472 (or note as override-routed)
- `src/Humans.Infrastructure/Repositories/Users/UserRepository.cs` — remove anonymization writes at lines ~274-277, ~320-323, ~425-428, ~496-499 (rename / merge / purge / deletion); remove `GetByNormalizedEmailAsync` if no remaining callers; remove `GetContactUsersAsync`
- `src/Humans.Application/Interfaces/Users/IUserService.cs` — remove `GetContactUsersAsync`
- `src/Humans.Application/Services/Users/UserService.cs` — remove `GetContactUsersAsync` impl
- `src/Humans.Application/Interfaces/Repositories/IUserRepository.cs` — remove `GetContactUsersAsync` and (if no callers) `GetByNormalizedEmailAsync`
- `src/Humans.Web/Extensions/Sections/ProfileSectionExtensions.cs` — remove DI registration for `IContactService` + alias
- `src/Humans.Web/Configuration/AdminNavigation.cs` (or wherever `/Contacts` link lives) — remove nav link
- `tests/Humans.Application.Tests/Architecture/IdentityColumnWriteRestrictionsTests.cs` — clear `IsExemptType` list, add `set_NormalizedUserName` / `set_UserName` to forbidden setters, add property-read scan against the 4 properties (User + IdentityUser declared) outside exempt files
- `tests/Humans.Integration.Tests/Infrastructure/HumansWebApplicationFactory.cs` — replace `db.Users.Where(u => u.Email == ...)` query at line 150
- `tests/Humans.Application.Tests/Services/MagicLinkServiceTests.cs` — adjust mocks if `FindUserByAnyEmailAsync` removed

### Created
- `src/Humans.Infrastructure/Identity/HumansUserStore.cs` — custom user store routing email-lookup methods through `IUserEmailService`
- `src/Humans.Infrastructure/Migrations/{ts}_DropIdentityEmailColumns.cs` — drops 4 columns + 2 indexes; preceded by defensive idempotent `INSERT ... WHERE NOT EXISTS`
- `tests/Humans.Application.Tests/Identity/HumansUserStoreTests.cs` — unit tests for the routed methods + throwing setters

### Deleted (entire `/Contacts` surface)
- `src/Humans.Web/Controllers/ContactsController.cs`
- `src/Humans.Web/Views/Contacts/Index.cshtml`
- `src/Humans.Web/Views/Contacts/Detail.cshtml`
- `src/Humans.Web/Views/Contacts/Create.cshtml`
- `src/Humans.Web/Models/ContactViewModels.cs`
- `src/Humans.Application/Services/Profile/ContactService.cs`
- `src/Humans.Application/Interfaces/Profiles/IContactService.cs`
- `src/Humans.Application/DTOs/AdminContactRow.cs`
- Tests covering `ContactService` / `ContactsController` (search post-modification)
- Localization resx entries for `/Contacts` views

---

## Task Ordering Rationale

Read sweep first, then property override, then column drop. Reasoning:

1. Read sweep (Tasks 3-9) makes every code path reach UserEmails directly. Once swept, the code no longer depends on the columns, so dropping them later doesn't break anything.
2. Anonymization-write removal (Task 10) must happen **before** the virtual setter throws (Task 11), or those code paths throw at runtime when called.
3. Virtual property override + UserConfiguration.Ignore + HumansUserStore (Tasks 11-13) must happen **before** the migration drops columns, or EF queries trying to read/write the columns fail.
4. Migration drops columns last (Task 14), after all code paths are routed through the override and through UserEmails.
5. Architecture test (Task 2) goes first as the failing baseline that captures every offender at the start, then re-runs at each task to confirm progress.
6. ContactService/`/Contacts` deletion (Task 8.5) folds in alongside the read sweep — it eliminates the only callers of `GetContactUsersAsync` + `IUserService.GetByEmailOrAlternateAsync` (in the ContactService specifically), letting Task 4 simplify.

---

## Task 1: Worktree + branch (already done — record only)

- [x] Worktree at `.worktrees/pr2-identity-surgery`, branch `email-oauth-pr2-identity-surgery`, based on `origin/main` (which carries PR 1).

---

## Task 2: Update architecture test — failing baseline

The arch test from PR 1 forbids writes to `set_Email` / `set_NormalizedEmail` / `set_EmailConfirmed` / `set_UserName` in Application + Web (DevLoginController exempt for `set_UserName = id.ToString()`). PR 2 expands this to:
- Add `set_NormalizedUserName` to the forbidden setters list.
- **Remove the DevLoginController exemption** for `set_UserName` (Task 7 will switch DevLogin to UserEmail-row-based persona seeding without setting UserName).
- Add a parallel scan for **property reads** of `Email` / `NormalizedEmail` / `EmailConfirmed` / `NormalizedUserName` solution-wide, exempt only:
  - `Humans.Domain/Entities/User.cs` (the override implementations themselves)
  - `Humans.Infrastructure/Identity/HumansUserStore.cs` (the Identity contract)
  - `Humans.Infrastructure/Data/Configurations/Users/UserConfiguration.cs` (the `Ignore()` mapping)
  - The migrations folder (older migrations reference the columns by name)

**Files:**
- Modify: `tests/Humans.Application.Tests/Architecture/IdentityColumnWriteRestrictionsTests.cs`

- [ ] **Step 1:** Extend the test:
  - Add `set_NormalizedUserName` to `ForbiddenSetters`.
  - Replace the `IsExemptType` list (currently `DevLoginController`, `DevelopmentDashboardSeeder`, `ContactService`) with the empty-set + the override-implementation exemptions listed above.
  - Add a `ForbiddenGetters` set: `get_Email`, `get_NormalizedEmail`, `get_EmailConfirmed`, `get_NormalizedUserName`. Same IL-walk pattern, same `IsUserOrIdentityUser` filter on the declaring type.
  - Update the failure message to reference PR 2.

- [ ] **Step 2:** Run the test, expect FAIL listing every current offender across Application + Web. The list should match the read-sweep targets in Tasks 3-9 plus any sites the spec didn't enumerate. Save the offender list to a scratch comment in the plan or memory; the count goes to zero by Task 14.

- [ ] **Step 3:** Commit:
  ```
  test(arch): expand Identity-column restrictions to reads + UserName setters (PR 2 baseline)
  ```

---

## Task 3: Sweep `MagicLinkService` reads

Three offenders confirmed via `reforge callers`:
- `MagicLinkService.cs:79` — `_userManager.FindByEmailAsync(email)` in `SendMagicLinkAsync` step 2 fallback
- `MagicLinkService.cs:143` — `_userManager.FindByEmailAsync(email)` in `FindUserByVerifiedEmailAsync` final fallback
- `MagicLinkService.cs:162` — `_userRepository.GetByNormalizedEmailAsync(...)` in `FindUserByAnyEmailAsync` step 2

Replacement strategy: `IUserEmailService` already exposes `GetUserIdByVerifiedEmailAsync(email)` (line 185 of `IUserEmailService.cs`) and `FindVerifiedEmailWithUserAsync(email)` (line 145). Both route through UserEmails. Use whichever the receiving method needs.

**Files:**
- Modify: `src/Humans.Application/Services/Auth/MagicLinkService.cs`

- [ ] **Step 1:** Replace line 79 with a UserEmails-backed lookup. The surrounding code in `SendMagicLinkAsync` is the "no row in `user_emails` matched" fallback — under PR 2 it's dead because UserEmails IS the only authoritative source. Either delete the branch or replace the call with `null` (no fallback). Verify by reading the surrounding context that the branch is reachable; if not, delete.

- [ ] **Step 2:** Replace line 143 (`FindUserByVerifiedEmailAsync`'s final fallback) similarly. The whole method may collapse to a single `FindVerifiedEmailWithUserAsync` call followed by `users.GetByIdAsync(userEmail.UserId)`.

- [ ] **Step 3:** Replace line 162 (`FindUserByAnyEmailAsync` step 2 — the unverified-email + User.Email match path). After PR 2 the column is gone, so "User.Email" returns whatever the override computes (first IsVerified row by IsNotificationTarget desc — which is identical to the verified-row lookup we already do above it). Conclusion: `FindUserByAnyEmailAsync` collapses to the unverified-only branch. Either inline that into the two callers or delete the method entirely.

- [ ] **Step 4:** Re-run reforge:
  ```bash
  reforge callers Humans.Application.Services.Auth.MagicLinkService.FindUserByAnyEmailAsync
  ```
  If zero remaining callers, delete the method + the test mocks at `MagicLinkServiceTests.cs` lines 50, 101, 174, 284 are unaffected since they mock `FindVerifiedEmailWithUserAsync`, not `FindUserByAnyEmailAsync`. Verify `AccountController.ExternalLoginCallback:209` is also gone (Task 9) before declaring deletion safe.

- [ ] **Step 5:** Run arch test; expect 3 fewer offenders.

- [ ] **Step 6:** Run MagicLinkService tests; update any that mocked `_userManager.FindByEmailAsync` or `_userRepository.GetByNormalizedEmailAsync`.

- [ ] **Step 7:** Commit:
  ```
  refactor(magic-link): route email lookups through UserEmails (PR 2)
  ```

---

## Task 4: Sweep `AccountProvisioningService.FindOrCreateUserByEmailAsync` step 2

**Files:**
- Modify: `src/Humans.Application/Services/Users/AccountProvisioningService.cs:93`

- [ ] **Step 1:** Replace `var matchingUser = await _userRepository.GetByEmailOrAlternateAsync(...)` with a UserEmails-backed lookup. Sequence:
  1. `IUserEmailService.GetUserIdByVerifiedEmailAsync(email, ct)` → returns `Guid?`
  2. If non-null, `IUserService.GetByIdAsync(userId, ct)` → returns the User

- [ ] **Step 2:** Run arch test; expect 1 fewer offender.

- [ ] **Step 3:** Run `AccountProvisioningServiceTests`; update mocks if any test stubbed `GetByEmailOrAlternateAsync`.

- [ ] **Step 4:** Commit:
  ```
  refactor(account-provisioning): UserEmails lookup replaces GetByEmailOrAlternateAsync
  ```

---

## Task 5: Sweep `GoogleWorkspaceSyncService` + `GoogleAdminService`

Discovered during planning via reforge — not in the spec's PR 2 enumeration. Same pattern as Task 4.

**Files:**
- Modify: `src/Humans.Application/Services/GoogleIntegration/GoogleWorkspaceSyncService.cs:306`
- Modify: `src/Humans.Application/Services/GoogleIntegration/GoogleAdminService.cs:176`

- [ ] **Step 1:** Replace each call to `_userService.GetByEmailOrAlternateAsync(...)` with the same `GetUserIdByVerifiedEmailAsync` → `GetByIdAsync` chain.

- [ ] **Step 2:** If `IUserService.GetByEmailOrAlternateAsync` (and the underlying repo method + the `IUserRepository` declaration) now have zero remaining callers, delete them all in this commit. Run reforge to confirm:
  ```bash
  reforge callers Humans.Application.Interfaces.Users.IUserService.GetByEmailOrAlternateAsync
  reforge callers Humans.Application.Interfaces.Repositories.IUserRepository.GetByEmailOrAlternateAsync
  ```

- [ ] **Step 3:** Run arch test; expect 2 fewer offenders.

- [ ] **Step 4:** Run Google service tests; update mocks.

- [ ] **Step 5:** Commit:
  ```
  refactor(google): UserEmails lookup replaces GetByEmailOrAlternateAsync (+ delete the method if dead)
  ```

---

## Task 6: Sweep `DevLoginController`

**Files:**
- Modify: `src/Humans.Web/Controllers/DevLoginController.cs:116, 185`

- [ ] **Step 1:** Replace line 116 (`?? await _userManager.FindByEmailAsync(email)` post-seed path) with a UserEmails lookup. The fallback exists for "the seed just inserted a user; UserManager hasn't seen the row yet" — under the column-drop world, UserManager goes through `HumansUserStore` which uses UserEmails, so the fallback collapses to the same path. Replace with `IUserEmailService.GetUserIdByVerifiedEmailAsync(email)` → `IUserService.GetByIdAsync(...)`.

- [ ] **Step 2:** Replace line 185 (`var byEmail = await _userManager.FindByEmailAsync(email)` legacy persona path) similarly. This is the "dev runs against a DB cloned from prod and there's a legacy persona" branch — under the override world, UserManager will return that user via UserEmails. So either delete the branch (it's redundant with `FindByIdAsync`) or rewrite as `GetUserIdByVerifiedEmailAsync` → `GetByIdAsync`.

- [ ] **Step 3:** Also remove the persona-seed `UserName = id.ToString()` write — under PR 2's setter-throws world, even setting UserName during seed throws. The seed should construct the User with no UserName at all (rely on Identity defaulting to null, which is fine since `RequireUniqueEmail = false` was set in PR 1 and `NormalizedUserName` is computed by Identity from UserName at validation time but `UserConfiguration.Ignore()` exempts it from EF round-trip).

  **Caveat:** `IdentityUser<Guid>.UserName` is a settable property without override possibility from outside Identity's constraints — once we override it with a `set => throw`, Identity's own internal calls (`UserManager.CreateAsync` calls `_store.SetUserNameAsync` which calls `user.UserName = ...`) will throw. We need `HumansUserStore.SetUserNameAsync` to be the routing point that swallows the call (or to allow the set on User itself). **Decision at Task 11/12 implementation time.** Option A: don't override `UserName`/`NormalizedUserName` setters at all (keep them column-backed in memory; just `Ignore()` them in EF mapping). Option B: override both, route writes through HumansUserStore.

  **Recommended:** Option A is simpler. The columns are gone but the `User` instance carries them in memory for Identity's internal coordination only; nothing persists. Verify `UserConfiguration.Ignore()` is sufficient.

- [ ] **Step 4:** Run arch test; expect 2 fewer offenders.

- [ ] **Step 5:** Run dev-login + browser smoke (manual): `/dev/login/admin` on a clean DB, then on a DB cloned from QA. Both must succeed.

- [ ] **Step 6:** Commit:
  ```
  refactor(dev-login): UserEmails lookup replaces FindByEmailAsync; drop UserName seed write
  ```

---

## Task 7: Sweep `DevelopmentDashboardSeeder`

**Files:**
- Modify: `src/Humans.Web/Infrastructure/DevelopmentDashboardSeeder.cs:422-424`

- [ ] **Step 1:** Replace the `u.Email LIKE dev-human-*@seed.local` filter. The seed marker `@seed.local` lives in UserEmail rows post-PR-1. Switch to:
  ```csharp
  var devUserIds = await _db.UserEmails
      .Where(ue => ue.Email.EndsWith(DevUserEmailSuffix)
                && ue.Email.StartsWith(DevUserEmailPrefix))
      .Select(ue => ue.UserId)
      .Distinct()
      .ToListAsync(ct);
  // then load users by Id
  ```

- [ ] **Step 2:** Run arch test; expect 1 fewer offender. Run a dev-dashboard reset to confirm the seed/reset cycle works end-to-end.

- [ ] **Step 3:** Commit:
  ```
  refactor(dev-dashboard): seed-marker filter on UserEmails instead of User.Email
  ```

---

## Task 8: Sweep integration test fixture

**Files:**
- Modify: `tests/Humans.Integration.Tests/Infrastructure/HumansWebApplicationFactory.cs:150`

- [ ] **Step 1:** Replace `.FirstOrDefaultAsync(u => u.Email == email)` with the same UserEmails join pattern from Task 7. Use the test fixture's existing DbContext directly.

- [ ] **Step 2:** Run integration tests; expect green.

- [ ] **Step 3:** Commit:
  ```
  test(integration): fixture sign-in helper looks up via UserEmails
  ```

---

## Task 8.5: Delete `/Contacts` admin surface + `ContactService`

Issue §8. The `/Contacts` admin surface is removed wholesale per Peter's direction ("contacts isn't really a thing").

**Files:** see the Deleted block in the File Map above.

- [ ] **Step 1:** Delete the controller, three views, view models, service, interface, DTO. Use `git rm` so the deletion is staged.

- [ ] **Step 2:** Remove DI registration in `src/Humans.Web/Extensions/Sections/ProfileSectionExtensions.cs`:
  - Remove the `using ProfilesContactService = Humans.Application.Services.Profile.ContactService;` alias (line 13).
  - Remove `services.AddScoped<IContactService, ProfilesContactService>();` (line 67).

- [ ] **Step 3:** Remove the `/Contacts` link from admin nav. Find the nav source:
  ```bash
  grep -rn "/Contacts\|Contacts.Index\|nameof(ContactsController)" src/Humans.Web
  ```

- [ ] **Step 4:** Remove `IUserService.GetContactUsersAsync` + `UserService.GetContactUsersAsync` impl + `IUserRepository.GetContactUsersAsync` declaration + `UserRepository.GetContactUsersAsync` impl. Reforge confirms ContactService is the only caller. Delete the four method bodies/declarations.

- [ ] **Step 5:** Verify `IUserEmailService.FindVerifiedEmailWithUserAsync` retains its other callers (MagicLinkService — confirmed via reforge). KEEP this method.

- [ ] **Step 6:** Search for and delete any localization resx entries for the deleted views:
  ```bash
  grep -rn "Contacts_Index\|Contacts_Detail\|Contacts_Create" src/Humans.Web/Resources
  ```

- [ ] **Step 7:** Search for and delete any remaining tests:
  ```bash
  reforge references Humans.Application.Services.Profile.ContactService
  reforge references Humans.Web.Controllers.ContactsController
  ```

- [ ] **Step 8:** Build the solution:
  ```bash
  dotnet build Humans.slnx -v quiet
  ```
  Expect green. Any error → trace remaining reference, delete or sweep.

- [ ] **Step 9:** Commit:
  ```
  refactor: remove /Contacts admin surface (controller, service, views) — orphan concept
  ```

---

## Task 9: Restructure `AccountController.ExternalLoginCallback`

Remove two branches:
- **Lockout-relink branch (lines 104-158):** rationale per spec — superseded by PR 1's "preserve at least one auth method" deletion guard.
- **`FindUserByAnyEmailAsync` branch (lines 207-242):** rationale — once `User.Email` column is gone, the "match by unverified email or User.Email" path collapses to "match by any email" which is the same as the verified-email branch above it (under override semantics, `User.Email` = first IsVerified UserEmail row).

**Files:**
- Modify: `src/Humans.Web/Controllers/AccountController.cs`

- [ ] **Step 1:** Delete lines 104-158 (the entire `if (result.IsLockedOut)` block). Keep only `return RedirectToAction(nameof(Login), new { returnUrl, error = "lockedout" });` as the lockout response.

- [ ] **Step 2:** Delete lines 207-242 (the `existingByAnyEmail` branch entirely).

- [ ] **Step 3:** Verify `MagicLinkService.FindUserByAnyEmailAsync` is now uncalled from anywhere; if so, finalize Task 3 Step 4 deletion.

- [ ] **Step 4:** Run web auth tests + browser smoke for OAuth (existing user with verified email; existing user with no UserEmail at all — must hit the "create new account" branch).

- [ ] **Step 5:** Commit:
  ```
  refactor(auth): drop lockout-relink + FindUserByAnyEmailAsync branches in OAuth callback
  ```

---

## Task 10: Remove anonymization writes in `UserRepository`

These writes set `user.Email = "deleted-..."` / `user.UserName = "deleted-..."` etc. They become exception-throwing once Task 11 lands the virtual setters, so they must be removed before that.

**Files:**
- Modify: `src/Humans.Infrastructure/Repositories/Users/UserRepository.cs`

- [ ] **Step 1:** Read the four write sites:
  ```bash
  grep -n "user\.Email\s*=\|user\.UserName\s*=\|user\.NormalizedEmail\s*=\|user\.EmailConfirmed\s*=" src/Humans.Infrastructure/Repositories/Users/UserRepository.cs
  ```
  Expected: 4 blocks (rename ~274-277, merge ~320-323, purge ~425-428, deletion ~496-499). Line numbers may shift; verify each in context.

- [ ] **Step 2:** Delete each `user.Email = ...` / `user.UserName = ...` / `user.NormalizedEmail = ...` / `user.EmailConfirmed = ...` assignment block. The surrounding logic (`UserEmailService.RemoveAllEmailsAsync`, audit-log writes, soft-delete flags) stays.

- [ ] **Step 3:** Run anonymization tests:
  ```bash
  dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~UserRepository" --filter "FullyQualifiedName~AnonymizationTests"
  ```
  Expected: PASS. Tests that asserted `user.Email == "deleted-..."` should be updated to assert via `UserEmail` rows (verify no `UserEmails` exist for the user post-anonymization).

- [ ] **Step 4:** Commit:
  ```
  refactor(user-repo): drop Identity-column anonymization writes (no-ops once columns drop)
  ```

---

## Task 11: Virtual property overrides on `User`

**Files:**
- Modify: `src/Humans.Domain/Entities/User.cs`

- [ ] **Step 1:** Add at the top of the User class (or wherever fits the existing organisation):
  ```csharp
  /// <summary>
  /// Overrides the inherited Identity property to compute from UserEmails.
  /// Returns the first verified row ordered by IsNotificationTarget desc, or
  /// null if the user has no verified UserEmail. Setter throws — UserEmails
  /// is the only authoritative source post-PR-2.
  /// </summary>
  public override string? Email
  {
      get => UserEmails
          .Where(e => e.IsVerified)
          .OrderByDescending(e => e.IsNotificationTarget)
          .Select(e => e.Email)
          .FirstOrDefault();
      set => throw new NotSupportedException(
          "User.Email is computed from UserEmails. Modify UserEmail rows via IUserEmailService instead.");
  }

  public override string? NormalizedEmail
  {
      get => Email?.ToUpperInvariant();
      set => throw new NotSupportedException(
          "User.NormalizedEmail is derived from User.Email. Modify UserEmail rows via IUserEmailService instead.");
  }

  public override bool EmailConfirmed
  {
      get => UserEmails.Any(e => e.IsVerified);
      set => throw new NotSupportedException(
          "User.EmailConfirmed is derived from UserEmails. Modify UserEmail rows via IUserEmailService instead.");
  }
  ```

- [ ] **Step 2:** **`UserName` / `NormalizedUserName` decision.** Identity's `UserManager.CreateAsync` calls `_store.SetUserNameAsync(user, name)` internally, which the default `UserStore` translates to `user.UserName = name`. If we override `User.UserName` with a throwing setter, that path explodes.

  **Choice:** **Don't override `UserName`/`NormalizedUserName` setters.** Leave them column-backed in memory (the EF mapping `Ignore()` ensures they don't round-trip to the DB). They retain whatever Identity puts in them per-request, which is fine because nothing persists.

  **However,** the GETTERS for these may still be queried by Identity validators (e.g., `RequireUniqueUserName`). Since we set `UserName = id.ToString()` in PR 1's user creation paths, the in-memory value is sensible. No override needed — they remain inherited from `IdentityUser<Guid>` with no behavioral change beyond the EF mapping ignoring them.

  **Conclusion:** Step 1 covers `Email`, `NormalizedEmail`, `EmailConfirmed` only. `UserName` and `NormalizedUserName` are not overridden — they're just `Ignore()`-d in `UserConfiguration` (Task 12).

- [ ] **Step 3:** Update `User.GetEffectiveEmail()` at line 76. Current code: `return notificationEmail?.Email ?? Email;`. The fallback `?? Email` now hits the override which already returns the first IsVerified row by IsNotificationTarget desc — which is the same row `notificationEmail` already found (or a different row if `notificationEmail` was null). Simplify:
  ```csharp
  public string? GetEffectiveEmail() => Email;
  ```
  (The override does all the work.)

- [ ] **Step 4:** Build. Expect compile error if any code still does `new User { Email = ... }` — Tasks 4-9 should have removed all of these. Trace remaining → fix.

- [ ] **Step 5:** Run unit tests covering User; tests that constructed `User` with `Email = ...` need to be updated to populate `UserEmails` instead.

- [ ] **Step 6:** Commit:
  ```
  feat(domain): override User.Email/NormalizedEmail/EmailConfirmed to compute from UserEmails
  ```

---

## Task 12: `UserConfiguration` — `Ignore()` the four properties

**Files:**
- Modify: `src/Humans.Infrastructure/Data/Configurations/Users/UserConfiguration.cs`

- [ ] **Step 1:** Add inside the `Configure(EntityTypeBuilder<User> builder)` body:
  ```csharp
  // Identity columns dropped per email-identity-decoupling spec PR 2.
  // The User entity overrides Email/NormalizedEmail/EmailConfirmed to compute
  // from UserEmails. UserName/NormalizedUserName remain in memory for
  // Identity's internal coordination but don't round-trip to the DB.
  builder.Ignore(u => u.Email);
  builder.Ignore(u => u.NormalizedEmail);
  builder.Ignore(u => u.EmailConfirmed);
  builder.Ignore(u => u.NormalizedUserName);
  // UserName is auto-mapped by Identity but the column will be dropped — we
  // need to either Ignore() or tell EF to map it without expecting a column.
  // Go with Ignore() to be explicit; Identity stores UserName in memory only.
  builder.Ignore(u => u.UserName);
  ```

- [ ] **Step 2:** Build the solution:
  ```bash
  dotnet build Humans.slnx -v quiet
  ```
  Expect green. EF model-snapshot validation should not complain since `Ignore()` is the standard mechanism.

- [ ] **Step 3:** Commit:
  ```
  feat(efcore): Ignore() Identity columns on User configuration (decoupling PR 2)
  ```

---

## Task 13: `HumansUserStore` — custom Identity user store (TDD)

This routes Identity's `IUserEmailStore<User>.FindByEmailAsync` / `GetEmailAsync` / `GetEmailConfirmedAsync` through `IUserEmailService`. Write methods (`SetEmailAsync`, `SetEmailConfirmedAsync`) throw `NotSupportedException`.

**Files:**
- Create: `src/Humans.Infrastructure/Identity/HumansUserStore.cs`
- Create: `tests/Humans.Application.Tests/Identity/HumansUserStoreTests.cs`
- Modify: `src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs`

- [ ] **Step 1: Write failing tests.** Cover:
  - `FindByEmailAsync(normalizedEmail)` returns the User whose UserEmails contains a verified row matching the email (case-insensitive on `IUserEmailService.GetUserIdByVerifiedEmailAsync`).
  - `FindByEmailAsync` returns null when no UserEmail matches.
  - `GetEmailAsync(user)` returns `User.Email` (which is the override).
  - `GetEmailConfirmedAsync(user)` returns `User.EmailConfirmed` (override).
  - `SetEmailAsync` throws `NotSupportedException`.
  - `SetEmailConfirmedAsync` throws `NotSupportedException`.
  - `SetNormalizedEmailAsync` is a no-op (Identity calls this internally; throwing breaks `CreateAsync`).
  - `GetNormalizedEmailAsync` returns `User.NormalizedEmail` (override).

  Mock `IUserEmailService` + `IUserService` with NSubstitute. Look at `tests/Humans.Application.Tests/Services/Auth/MagicLinkServiceTests.cs` for the existing NSubstitute pattern.

- [ ] **Step 2: Run tests, expect FAIL** — `HumansUserStore` doesn't exist yet.

- [ ] **Step 3: Implement.** The store inherits from `UserStoreBase<User, Guid, IdentityUserClaim<Guid>, IdentityUserLogin<Guid>, IdentityUserToken<Guid>>` or similar — easier path: implement only `IUserStore<User>`, `IUserEmailStore<User>`, `IUserPasswordStore<User>` directly. Most methods delegate to the underlying `UserStore<User, IdentityRole<Guid>, HumansDbContext, Guid>` for non-email operations (Create, Update, Delete, FindById, etc.).

  Sketch:
  ```csharp
  public sealed class HumansUserStore :
      UserStore<User, IdentityRole<Guid>, HumansDbContext, Guid>,
      IUserEmailStore<User>
  {
      private readonly IUserEmailService _userEmailService;
      private readonly IUserService _userService;

      public HumansUserStore(
          HumansDbContext context,
          IUserEmailService userEmailService,
          IUserService userService,
          IdentityErrorDescriber? describer = null)
          : base(context, describer)
      {
          _userEmailService = userEmailService;
          _userService = userService;
      }

      public override async Task<User?> FindByEmailAsync(
          string normalizedEmail, CancellationToken cancellationToken = default)
      {
          var userId = await _userEmailService.GetUserIdByVerifiedEmailAsync(
              normalizedEmail, cancellationToken);
          return userId is null ? null : await _userService.GetByIdAsync(userId.Value, cancellationToken);
      }

      public override Task<string?> GetEmailAsync(User user, CancellationToken cancellationToken = default)
          => Task.FromResult(user.Email);

      public override Task<bool> GetEmailConfirmedAsync(User user, CancellationToken cancellationToken = default)
          => Task.FromResult(user.EmailConfirmed);

      public override Task SetEmailAsync(User user, string? email, CancellationToken cancellationToken = default)
          => throw new NotSupportedException(
              "User.Email is derived from UserEmails. Use IUserEmailService.AddVerifiedEmailAsync or AddOAuthEmailAsync.");

      public override Task SetEmailConfirmedAsync(User user, bool confirmed, CancellationToken cancellationToken = default)
          => throw new NotSupportedException(
              "User.EmailConfirmed is derived from UserEmails. Verify a UserEmail row instead.");

      public override Task<string?> GetNormalizedEmailAsync(User user, CancellationToken cancellationToken = default)
          => Task.FromResult(user.NormalizedEmail);

      public override Task SetNormalizedEmailAsync(User user, string? normalizedEmail, CancellationToken cancellationToken = default)
          => Task.CompletedTask;  // no-op: Identity calls this on Create/Update; we don't persist.
  }
  ```

  **Note:** `UserStore<>` is the EF-backed default store. By inheriting and overriding only the email methods, we keep all the Login/Token/Role plumbing for free. The base `UserStore` does not depend on the Email column for its non-email operations — verify in source if uncertain.

- [ ] **Step 4: Run tests, expect PASS.**

- [ ] **Step 5:** Register in DI. Modify `src/Humans.Web/Extensions/InfrastructureServiceCollectionExtensions.cs`:
  ```csharp
  // Replace .AddUserStore<UserStore<...>>() (or whatever the current default registration is)
  // with .AddUserStore<HumansUserStore>().
  // Identity expects UserStore<TUser, TRole, TContext, TKey>; HumansUserStore extends that.
  ```
  Verify by reading the current `AddIdentity` configuration block.

- [ ] **Step 6:** Build + run integration tests covering OAuth + magic-link flows. If any breaks, trace and fix before committing.

- [ ] **Step 7:** Commit:
  ```
  feat(identity): HumansUserStore routes email lookups through UserEmails
  ```

---

## Task 14: EF migration — drop Identity columns + defensive backfill

**Files:**
- Create: `src/Humans.Infrastructure/Migrations/{ts}_DropIdentityEmailColumns.cs`

- [ ] **Step 1:** Generate the migration:
  ```bash
  dotnet ef migrations add DropIdentityEmailColumns -p src/Humans.Infrastructure -s src/Humans.Web
  ```
  Inspect the generated `Up()` — EF will emit `DropColumn` calls for the four properties because of `Ignore()`. It should also drop the `EmailIndex` and `UserNameIndex` HasIndex calls if those were declared in `UserConfiguration` (verify).

- [ ] **Step 2:** Edit the migration to add the defensive backfill **before** the column drops. Insert a `migrationBuilder.Sql(...)` call at the top of `Up()`:
  ```csharp
  protected override void Up(MigrationBuilder migrationBuilder)
  {
      // Defensive backfill: catch any orphan Users the PR 1 admin button missed.
      // Idempotent: WHERE NOT EXISTS guards against re-runs.
      migrationBuilder.Sql(@"
          INSERT INTO user_emails
              (id, user_id, email, is_verified, is_notification_target, is_oauth,
               visibility, display_order, created_at, updated_at)
          SELECT
              gen_random_uuid(),
              u.""Id"",
              u.""Email"",
              COALESCE(u.""EmailConfirmed"", false),
              COALESCE(u.""EmailConfirmed"", false),  -- IsNotificationTarget = IsVerified
              EXISTS (SELECT 1 FROM ""AspNetUserLogins"" l WHERE l.""UserId"" = u.""Id""),
              2,  -- ContactFieldVisibility.BoardOnly
              0,
              NOW() AT TIME ZONE 'UTC',
              NOW() AT TIME ZONE 'UTC'
          FROM ""AspNetUsers"" u
          WHERE u.""Email"" IS NOT NULL
            AND NOT EXISTS (
                SELECT 1 FROM user_emails e WHERE e.user_id = u.""Id""
            );
      ");

      // Drop the indexes first (their underlying columns are about to vanish).
      migrationBuilder.DropIndex(
          name: "EmailIndex",
          table: "AspNetUsers");
      migrationBuilder.DropIndex(
          name: "UserNameIndex",
          table: "AspNetUsers");

      // Drop the four columns.
      migrationBuilder.DropColumn(name: "Email", table: "AspNetUsers");
      migrationBuilder.DropColumn(name: "NormalizedEmail", table: "AspNetUsers");
      migrationBuilder.DropColumn(name: "EmailConfirmed", table: "AspNetUsers");
      migrationBuilder.DropColumn(name: "NormalizedUserName", table: "AspNetUsers");
      migrationBuilder.DropColumn(name: "UserName", table: "AspNetUsers");
  }
  ```

  **Verify column names match what's actually in the DB.** PostgreSQL is case-sensitive when columns were quoted at creation; the table is `AspNetUsers` with quoted PascalCase column names. The Sql block above uses double quotes — match what you see in earlier migrations in the repo.

  **Verify `user_emails` schema** — the column list (`is_verified`, `is_notification_target`, `is_oauth`, `visibility`, `display_order`, `created_at`, `updated_at`) is what `UserEmail` defines today. Read `src/Humans.Infrastructure/Migrations/HumansDbContextModelSnapshot.cs` for the exact column ordering and types before committing.

- [ ] **Step 3:** **Run the EF migration reviewer agent** per CLAUDE.md gate:
  ```bash
  cat .claude/agents/ef-migration-reviewer.md
  ```
  Spawn the agent with the migration file as input. Resolve any CRITICAL findings before proceeding.

- [ ] **Step 4:** Apply locally on a test DB:
  ```bash
  dotnet ef database update -p src/Humans.Infrastructure -s src/Humans.Web
  ```
  Verify no errors. Inspect schema:
  ```sql
  SELECT column_name FROM information_schema.columns WHERE table_name = 'AspNetUsers';
  -- expect Email/NormalizedEmail/EmailConfirmed/UserName/NormalizedUserName ABSENT
  ```

- [ ] **Step 5:** Run the full test suite:
  ```bash
  dotnet test Humans.slnx -v quiet
  ```
  Expect green.

- [ ] **Step 6:** Commit:
  ```
  feat(migration): drop Identity email/username columns + indexes (PR 2)

  Includes idempotent defensive INSERT INTO user_emails ... WHERE NOT EXISTS
  before the column drops to catch any orphan Users that the PR 1 admin
  backfill button missed. App always boots — no startup orphan guard.

  Reviewed: .claude/agents/ef-migration-reviewer.md
  ```

---

## Task 15: Final architecture-test verification

- [ ] **Step 1:** Run the arch test:
  ```bash
  dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~IdentityColumnWriteRestrictionsTests"
  ```
  Expected: PASS, zero offenders.

- [ ] **Step 2:** Run the full suite:
  ```bash
  dotnet test Humans.slnx -v quiet
  ```
  Expected: green.

- [ ] **Step 3:** Run `dotnet format Humans.slnx`:
  ```bash
  dotnet format Humans.slnx
  ```
  PR 1 CI failed on whitespace. Don't repeat that.

- [ ] **Step 4:** If anything fails, fix and re-verify before proceeding.

---

## Task 16: Browser smoke

Per CLAUDE.md: UI/auth changes need browser verification, not just type-checking + tests.

- [ ] **Step 1:** Start the dev server:
  ```bash
  dotnet run --project src/Humans.Web
  ```

- [ ] **Step 2:** Smoke each path on `http://nuc.home`:
  - [ ] **Magic-link sign-in (existing user via verified UserEmail):** request magic link with an email that has a verified UserEmail row, click link, expect successful sign-in.
  - [ ] **Magic-link sign-in (new user — verifies the no-create behavior):** request magic link with a never-seen email, expect "we sent a verification link" but no User created (PR 6 will add the create-from-magic-link funnel; PR 2 maintains lookup-only).
  - [ ] **Google OAuth sign-in (existing user, verified UserEmail matches Google email):** Google account with email that has a verified UserEmail row → sign in should auto-link.
  - [ ] **Google OAuth sign-in (existing user, NO matching UserEmail row):** Google account whose email is unknown → "create new account" path should fire (Task 9 confirms this branch is reachable).
  - [ ] **Dev login (existing persona by id):** `/dev/login/admin` against a DB cloned from QA → existing dev persona reused via `FindByIdAsync` (Task 6 path).
  - [ ] **Dev login (new persona seed):** `/dev/login/admin` against a fresh DB → new persona created.
  - [ ] **Multi-email user signs in via secondary email:** add a second verified UserEmail; log out; magic-link sign in via the secondary email; expect success.
  - [ ] **Admin user impersonation if any:** verify the impersonation flow still works (uses UserManager under the hood).

- [ ] **Step 3:** Migration's defensive backfill verification on a staging DB snapshot:
  - [ ] Restore a recent QA DB dump locally.
  - [ ] Manually delete a UserEmail row to create an orphan User.
  - [ ] Apply the migration; verify a new UserEmail row was inserted from `User.Email`.
  - [ ] Re-apply (idempotency); verify zero changes.

- [ ] **Step 4:** If any smoke fails, fix and re-verify before declaring done.

---

## Task 17: Push + open PR

- [ ] **Step 1:** Verify clean working tree (all commits made):
  ```bash
  git status
  ```

- [ ] **Step 2:** Push branch:
  ```bash
  git push -u origin email-oauth-pr2-identity-surgery
  ```

- [ ] **Step 3:** Open PR against `peterdrier/Humans:main`:
  ```bash
  gh pr create --repo peterdrier/Humans \
    --base main \
    --title "Email-identity decoupling PR 2 — virtual property overrides + custom UserStore + drop Identity columns" \
    --body-file .pr2-body.md
  ```
  (Draft the body inline using the issue's structure: Summary / What this PR does NOT do / Test plan / Continuity-rollback / Linked.)

- [ ] **Step 4:** Wait for CI green + Codex review. Per memory: "Done means Codex-clean, not just pushed." Address every review finding as a follow-up commit on the same branch. Do NOT cherry-pick fixes to `main` until CI is green on the PR.

---

## Self-Review Checklist

After implementing all tasks above:

1. **Spec coverage:** Every spec PR-2 bullet maps to a task:
   - Read sweep → Tasks 3-9
   - Architecture test → Task 2 (failing baseline) + Task 15 (zero offenders)
   - Virtual property overrides → Task 11
   - HumansUserStore → Task 13
   - UserConfiguration.Ignore → Task 12
   - Migration with defensive backfill → Task 14
   - UserRepository anonymization writes removed → Task 10
   - /Contacts removal (issue §8) → Task 8.5

2. **No startup guard.** The migration is idempotent on its own; if it fails partway, fix the cause and re-run. The application always boots; data fixes happen via admin actions or idempotent migrations, never via boot-time refusal. (HARD project rule.)

3. **EF migration reviewer agent ran on Task 14 before commit.** CRITICAL findings resolved.

4. **`dotnet format Humans.slnx` ran before each commit** to avoid PR 1's whitespace CI failure pattern.

5. **Pushed every 3-5 commits.** Don't wait until Task 17 to push the whole sequence.

6. **Reforge confirmed dead-code deletions:** `GetByEmailOrAlternateAsync` (Task 5), `GetContactUsersAsync` (Task 8.5), `FindUserByAnyEmailAsync` (Task 3 step 4) all verified to have zero remaining callers before deletion.

7. **PR diff against `peterdrier/Humans:main` is PR 2 changes only** — branch is based on `origin/main` which already has PR 1.
