# PR 1 — Decouple Identity-column Writes Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Stop writing the four ASP.NET Identity email/username columns (`Email`, `NormalizedEmail`, `EmailConfirmed`, `UserName` — `NormalizedUserName` is auto-derived) on User-creation paths, restructure the OAuth callback's new-User branch to remove silent-duplicate risk, replace the IsOAuth-based deletion guard with a "preserve at least one auth method" invariant, and add an admin-triggered backfill button that creates UserEmail rows for any orphan Users in the database. **Reads of those columns stay untouched until PR 2.**

**Architecture:** PR 1 of a 6-PR sequence to decouple human identity from `IdentityUser` columns. This PR is write-decoupling only — leaves all read paths intact, leaves the four columns in the schema, leaves anonymization writes in `UserRepository` (they become no-ops in PR 2 when columns drop). The admin backfill button is the preferred pre-deploy data-fix path; PR 2's column-drop migration also runs an idempotent defensive backfill so missed orphans are repaired before the drop.

**Tech Stack:** ASP.NET Core 10, ASP.NET Identity, EF Core 10 + PostgreSQL, NodaTime (`Instant`), AwesomeAssertions, xUnit, NetArchTest-style reflection-based architecture tests (already used in `tests/Humans.Application.Tests/Architecture/`).

**Spec:** `docs/superpowers/specs/2026-04-27-email-and-oauth-decoupling-design.md` (PR 1 section, revised 2026-04-28)

**Issue:** TBD — link added when issue is created

---

## File Map

### Modified
- `src/Humans.Web/Controllers/AccountController.cs` — `ExternalLoginCallback` new-User branch + `CompleteSignup` (stop Identity writes; restructure failure path)
- `src/Humans.Web/Controllers/DevLoginController.cs` — `EnsurePersonaAsync` + `SeedProfilelessUserAsync` (stop Identity writes)
- `src/Humans.Application/Services/Users/AccountProvisioningService.cs` — `FindOrCreateUserByEmailAsync` (stop Identity writes)
- `src/Humans.Application/Services/Profile/UserEmailService.cs` — `DeleteEmailAsync` deletion guard rewrite
- `src/Humans.Application/Interfaces/Profiles/IUserEmailService.cs` — no signature change; behavior change documented in XML doc
- `src/Humans.Web/Program.cs` — `RequireUniqueEmail = true → false` (line 169)

### Created
- `src/Humans.Application/Interfaces/Users/IUserEmailBackfillService.cs` — interface for admin-triggered orphan backfill
- `src/Humans.Application/Services/Users/UserEmailBackfillService.cs` — implementation
- `src/Humans.Web/Controllers/Admin/UsersAdminController.cs` — new controller hosting the backfill action (or extend an existing admin controller — see Task 8 reconnaissance step)
- `src/Humans.Web/Views/Admin/Users/BackfillUserEmails.cshtml` — confirm-then-run page with count display
- `tests/Humans.Application.Tests/Services/Users/UserEmailBackfillServiceTests.cs`
- `tests/Humans.Application.Tests/Services/Profile/UserEmailService_DeleteEmail_PreserveAuthMethodTests.cs`
- `tests/Humans.Application.Tests/Architecture/IdentityColumnWriteRestrictionsTests.cs`

---

## Task 1: Create the worktree (already done — record only)

**Files:** none — recorded in plan for completeness.

- [x] Worktree at `H:\source\Humans\.worktrees\pr1-decouple-identity-writes`, branch `email-oauth-pr1-decouple-writes` from `upstream/main`.

---

## Task 2: Update spec for revised PR 1 / PR 2 split (already done — record only)

**Files:**
- Modify: `docs/superpowers/specs/2026-04-27-email-and-oauth-decoupling-design.md`

- [x] Header revision note added (lines under Date).
- [x] PR 1 section rewritten (write-only scope + admin button, lockout-relink and FindUserByAnyEmailAsync stay).
- [x] PR 2 section absorbs read sweep + defensive backfill in the migration + UserRepository anonymization-write removal.
- [x] Migration section split into PR 1 (admin button) + PR 2 (column drop + guard).
- [x] Continuity table updated.

---

## Task 3: Architecture test forbidding writes in Application + Web

Write-restriction test must come first — it's the safety net that catches every write we miss.

**Files:**
- Create: `tests/Humans.Application.Tests/Architecture/IdentityColumnWriteRestrictionsTests.cs`

- [ ] **Step 1: Write the failing test**

```csharp
using System.Reflection;
using AwesomeAssertions;
using Humans.Application.Services.Users;
using Humans.Domain.Entities;
using Mono.Cecil;
using Mono.Cecil.Cil;
using Xunit;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing PR 1 of the email-identity-decoupling spec
/// (docs/superpowers/specs/2026-04-27-email-and-oauth-decoupling-design.md).
///
/// PR 1 stops writes to the four Identity-derived User columns
/// (Email, NormalizedEmail, EmailConfirmed, UserName) in Application and Web
/// assemblies. Reads remain unrestricted in PR 1. NormalizedUserName is
/// auto-derived by Identity from UserName and is not directly assigned.
///
/// Exemptions:
///  - Humans.Infrastructure (UserRepository anonymization writes — removed in PR 2 when columns drop).
///  - Humans.Web.Controllers.DevLoginController (transitional UserName = id.ToString() write — temporary; this exemption deletes in PR 2).
/// </summary>
public class IdentityColumnWriteRestrictionsTests
{
    private static readonly string[] ForbiddenSetters =
    {
        "set_Email",
        "set_NormalizedEmail",
        "set_EmailConfirmed",
        "set_UserName",
        "set_NormalizedUserName",
    };

    private static readonly (string Assembly, string TypePrefix)[] ScannedAssemblies =
    {
        ("Humans.Application", "Humans.Application."),
        ("Humans.Web", "Humans.Web."),
    };

    [HumansFact]
    public void NoApplicationOrWebCode_WritesIdentityEmailColumnsOnUser()
    {
        var offenders = new List<string>();

        foreach (var (assemblyName, _) in ScannedAssemblies)
        {
            var assemblyPath = ResolveAssemblyPath(assemblyName);
            using var module = ModuleDefinition.ReadModule(assemblyPath);

            foreach (var type in module.Types.SelectMany(Flatten))
            {
                if (IsExemptType(type.FullName))
                    continue;

                foreach (var method in type.Methods.Where(m => m.HasBody))
                {
                    foreach (var instr in method.Body.Instructions)
                    {
                        if (instr.OpCode != OpCodes.Callvirt && instr.OpCode != OpCodes.Call)
                            continue;

                        if (instr.Operand is not MethodReference mref)
                            continue;

                        if (!ForbiddenSetters.Contains(mref.Name))
                            continue;

                        // Only flag if the target is the User entity (or a base IdentityUser)
                        if (!IsUserOrIdentityUser(mref.DeclaringType))
                            continue;

                        offenders.Add($"{type.FullName}.{method.Name} -> {mref.DeclaringType.Name}.{mref.Name}");
                    }
                }
            }
        }

        offenders.Should().BeEmpty(
            because: "PR 1 of the email-identity-decoupling spec stops writes to the four Identity columns " +
                     "(Email/NormalizedEmail/EmailConfirmed/UserName) in Application + Web. " +
                     "If you see this fail, set UserName = user.Id.ToString() and leave the email columns " +
                     "at defaults; the UserEmail row carries the email going forward.");
    }

    private static IEnumerable<TypeDefinition> Flatten(TypeDefinition t) =>
        new[] { t }.Concat(t.NestedTypes.SelectMany(Flatten));

    private static bool IsExemptType(string fullName) =>
        // DevLoginController's UserName = id.ToString() during persona seed is transitional.
        // Removed in PR 2 once HumansUserStore + virtual overrides land.
        fullName.StartsWith("Humans.Web.Controllers.DevLoginController", StringComparison.Ordinal);

    private static bool IsUserOrIdentityUser(TypeReference t)
    {
        var current = t;
        while (current is not null)
        {
            if (current.FullName == typeof(User).FullName)
                return true;
            if (current.Name == "IdentityUser`1" || current.Name == "IdentityUser")
                return true;
            try { current = current.Resolve()?.BaseType; }
            catch { return false; }
        }
        return false;
    }

    private static string ResolveAssemblyPath(string assemblyName)
    {
        // Use the loaded test-host assembly's directory to find sibling DLLs.
        var hostDir = Path.GetDirectoryName(typeof(IdentityColumnWriteRestrictionsTests).Assembly.Location)!;
        var path = Path.Combine(hostDir, $"{assemblyName}.dll");
        if (!File.Exists(path))
            throw new FileNotFoundException($"Could not locate {assemblyName}.dll at {path}");
        return path;
    }
}
```

> **Note on Mono.Cecil:** Other architecture tests in this repo use `typeof(...).GetConstructors()` reflection. That works for shape (param types) but cannot detect property-setter call instructions inside method bodies. Mono.Cecil reads IL directly. Add `Mono.Cecil` PackageReference to `tests/Humans.Application.Tests/Humans.Application.Tests.csproj` if not present (use the version already used in any other test project, or latest stable 0.11.x).

- [ ] **Step 2: Add Mono.Cecil package + run test, verify it FAILS (counts current writes)**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~IdentityColumnWriteRestrictionsTests"`
Expected: FAIL — offenders list contains the 5 sites we will fix in Tasks 4–7.

- [ ] **Step 3: Commit the failing test**

```bash
git add tests/Humans.Application.Tests/Architecture/IdentityColumnWriteRestrictionsTests.cs tests/Humans.Application.Tests/Humans.Application.Tests.csproj
git commit -m "test(arch): add Identity-column write restrictions test (PR 1 baseline)"
```

---

## Task 4: Stop Identity writes in `AccountProvisioningService.FindOrCreateUserByEmailAsync`

**Files:**
- Modify: `src/Humans.Application/Services/Users/AccountProvisioningService.cs:118-126`

- [ ] **Step 1: Edit the new-user creation block**

Replace the existing initializer:

```csharp
        var newUser = new User
        {
            UserName = email,
            Email = email,
            DisplayName = resolvedDisplayName,
            ContactSource = source,
            CreatedAt = now,
            EmailConfirmed = true,
        };
```

With:

```csharp
        // Identity column writes decoupled per email-identity-decoupling spec PR 1.
        // The UserEmail row created below is now the source of truth for the
        // user's email; UserName is set to the User.Id GUID so Identity has a
        // unique non-empty UserName but the value carries no semantic meaning.
        var newUserId = Guid.NewGuid();
        var newUser = new User
        {
            Id = newUserId,
            UserName = newUserId.ToString(),
            DisplayName = resolvedDisplayName,
            ContactSource = source,
            CreatedAt = now,
        };
```

- [ ] **Step 2: Run the architecture test, verify offenders count drops by 1**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~IdentityColumnWriteRestrictionsTests"`
Expected: still FAIL, but with one fewer offender (no longer flags `AccountProvisioningService.FindOrCreateUserByEmailAsync`).

- [ ] **Step 3: Run AccountProvisioningService tests**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~AccountProvisioningService"`
Expected: PASS. If any test asserts `user.Email == "..."` after creation, update it to assert via the UserEmail row instead (`userEmailRepository.FindByNormalizedEmailAsync(...)`).

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Application/Services/Users/AccountProvisioningService.cs tests/
git commit -m "refactor(account-provisioning): stop writing User.Email/UserName/EmailConfirmed on creation"
```

---

## Task 5: Stop Identity writes in `AccountController.ExternalLoginCallback` new-User branch + restructure failure path

**Files:**
- Modify: `src/Humans.Web/Controllers/AccountController.cs:244-278`

The current code creates a new User with `Email`/`UserName`/`EmailConfirmed`, calls `CreateAsync`, then `AddLoginAsync`, then `AddOAuthEmailAsync`. If `AddLoginAsync` fails after `CreateAsync` succeeds, the User row persists with no auth method linked. With `RequireUniqueEmail = false` (Task 9), nothing prevents another caller from later creating the same User, so we must clean up.

- [ ] **Step 1: Replace the new-account-creation block**

Replace lines 244-278 (`// No existing account — create a new one` through the closing of the method) with:

```csharp
        // No existing account — create a new one.
        // Identity column writes decoupled per email-identity-decoupling spec PR 1:
        //   - UserName = id.ToString() (Identity needs a unique non-empty UserName)
        //   - Email/NormalizedEmail/EmailConfirmed left at defaults; UserEmail row is the source of truth.
        var newUserId = Guid.NewGuid();
        var newUser = new User
        {
            Id = newUserId,
            UserName = newUserId.ToString(),
            DisplayName = name ?? email,
            ProfilePictureUrl = pictureUrl,
            CreatedAt = _clock.GetCurrentInstant(),
            LastLoginAt = _clock.GetCurrentInstant()
        };

        var createResult = await _userManager.CreateAsync(newUser);
        if (!createResult.Succeeded)
        {
            foreach (var error in createResult.Errors)
                ModelState.AddModelError(string.Empty, error.Description);
            ViewData["ReturnUrl"] = returnUrl;
            return View(nameof(Login));
        }

        // Link the OAuth login. If linking fails after Create succeeded, undo the
        // user creation to avoid an orphan User with no auth method (would be
        // unreachable via either OAuth or magic link).
        var linkResult = await _userManager.AddLoginAsync(newUser, info);
        if (!linkResult.Succeeded)
        {
            try
            {
                await _userManager.DeleteAsync(newUser);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to clean up orphan user {UserId} after AddLoginAsync failure for {Provider}",
                    newUser.Id, info.LoginProvider);
            }

            foreach (var error in linkResult.Errors)
                ModelState.AddModelError(string.Empty, error.Description);
            ViewData["ReturnUrl"] = returnUrl;
            return View(nameof(Login));
        }

        // Create the OAuth UserEmail record for the login email.
        await _userEmailService.AddOAuthEmailAsync(newUser.Id, email);

        await _signInManager.SignInAsync(newUser, isPersistent: false);
        _logger.LogInformation("User created an account using {Provider}", info.LoginProvider);
        return RedirectToLocal(returnUrl);
    }
```

- [ ] **Step 2: Run the architecture test**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~IdentityColumnWriteRestrictionsTests"`
Expected: still FAIL but with one fewer offender (`AccountController.ExternalLoginCallback` no longer flagged).

- [ ] **Step 3: Run web auth tests**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~AccountController"`
Expected: PASS.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Controllers/AccountController.cs
git commit -m "refactor(auth): OAuth callback new-user branch — stop Identity writes, undo orphan on link failure"
```

---

## Task 6: Stop Identity writes in `AccountController.CompleteSignup` (magic-link signup)

**Files:**
- Modify: `src/Humans.Web/Controllers/AccountController.cs:386-411` (the `CompleteSignup` post handler)

- [ ] **Step 1: Replace the new-User initializer**

Replace:

```csharp
        var now = _clock.GetCurrentInstant();
        var user = new User
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? email : displayName.Trim(),
            CreatedAt = now,
            LastLoginAt = now
        };

        var createResult = await _userManager.CreateAsync(user);
        if (!createResult.Succeeded)
        {
            _logger.LogError("Failed to create user via magic link signup for {Email}: {Errors}",
                email, string.Join(", ", createResult.Errors.Select(e => e.Description)));
            return View("MagicLinkError");
        }

        // Create UserEmail record via service (non-OAuth, verified, notification target)
        await _userEmailService.AddOAuthEmailAsync(user.Id, email);
```

With:

```csharp
        var now = _clock.GetCurrentInstant();
        var newUserId = Guid.NewGuid();
        var user = new User
        {
            Id = newUserId,
            UserName = newUserId.ToString(),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? email : displayName.Trim(),
            CreatedAt = now,
            LastLoginAt = now
        };

        var createResult = await _userManager.CreateAsync(user);
        if (!createResult.Succeeded)
        {
            _logger.LogError("Failed to create user via magic link signup for {Email}: {Errors}",
                email, string.Join(", ", createResult.Errors.Select(e => e.Description)));
            return View("MagicLinkError");
        }

        // Create UserEmail record via service (verified, notification target).
        // Note: AddOAuthEmailAsync sets IsOAuth=true; this is misleading here
        // (this is a magic-link signup, not an OAuth signup). Pre-existing
        // behavior — the IsOAuth flag is repurposed in PR 3 to ProviderKey,
        // and this call site will be revisited at that point.
        await _userEmailService.AddOAuthEmailAsync(user.Id, email);
```

- [ ] **Step 2: Run the architecture test**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~IdentityColumnWriteRestrictionsTests"`
Expected: still FAIL but with one fewer offender.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Controllers/AccountController.cs
git commit -m "refactor(auth): magic-link signup — stop Identity writes on user creation"
```

---

## Task 7: Stop Identity writes in `DevLoginController` persona seed paths

DevLoginController is exempt from the architecture test (transitional `UserName = id.ToString()` is allowed during PR 1 to keep dev personas dedupable by ID), but we still want to stop the Email/EmailConfirmed writes for consistency and to validate the spec's "UserEmail row is the source of truth" hypothesis end-to-end in dev.

**Files:**
- Modify: `src/Humans.Web/Controllers/DevLoginController.cs:218-227` (`EnsurePersonaAsync`)
- Modify: `src/Humans.Web/Controllers/DevLoginController.cs:573-582` (`SeedProfilelessUserAsync`)

- [ ] **Step 1: Edit `EnsurePersonaAsync`**

Replace:

```csharp
        var user = new User
        {
            Id = id,
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = displayName,
            CreatedAt = now,
            LastLoginAt = now
        };
```

With:

```csharp
        var user = new User
        {
            Id = id,
            UserName = id.ToString(),
            DisplayName = displayName,
            CreatedAt = now,
            LastLoginAt = now
        };
```

- [ ] **Step 2: Edit `SeedProfilelessUserAsync`**

Replace:

```csharp
        var user = new User
        {
            Id = id,
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = displayName,
            CreatedAt = now,
            LastLoginAt = now
        };
```

With:

```csharp
        var user = new User
        {
            Id = id,
            UserName = id.ToString(),
            DisplayName = displayName,
            CreatedAt = now,
            LastLoginAt = now
        };
```

- [ ] **Step 3: Verify the architecture test still passes (DevLoginController is exempt)**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~IdentityColumnWriteRestrictionsTests"`
Expected: still FAIL only on `UserEmailService.DeleteEmailAsync` (deletion guard, Task 8) — no DevLoginController offenders flagged because of the exemption.

- [ ] **Step 4: Commit**

```bash
git add src/Humans.Web/Controllers/DevLoginController.cs
git commit -m "refactor(dev-login): stop writing Email/EmailConfirmed on persona seed (UserName=id.ToString)"
```

---

## Task 8: Replace `IsOAuth`-based deletion guard with "preserve at least one auth method"

**Files:**
- Modify: `src/Humans.Application/Services/Profile/UserEmailService.cs:245-273` (`DeleteEmailAsync`)
- Modify: `src/Humans.Application/Interfaces/Profiles/IUserEmailService.cs` — XML doc tweak on `DeleteEmailAsync`
- Create: `tests/Humans.Application.Tests/Services/Profile/UserEmailService_DeleteEmail_PreserveAuthMethodTests.cs`

- [ ] **Step 1: Write failing tests for the new guard**

Create `tests/Humans.Application.Tests/Services/Profile/UserEmailService_DeleteEmail_PreserveAuthMethodTests.cs`:

```csharp
using System.ComponentModel.DataAnnotations;
using AwesomeAssertions;
using Humans.Application.Interfaces.Profiles;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.AspNetCore.Identity;
using NodaTime;
using NSubstitute;
using NSubstitute.ReturnsExtensions;
using Xunit;
using Humans.Application.Services.Profile;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;

namespace Humans.Application.Tests.Services.Profile;

/// <summary>
/// PR 1 of email-identity-decoupling spec: replaces the IsOAuth-based deletion
/// guard with "preserve at least one auth method" — block delete only if the
/// user would be left with zero verified UserEmail rows AND zero AspNetUserLogins
/// rows. The OAuth-tied UserEmail row is now deletable as long as another auth
/// method remains.
/// </summary>
public class UserEmailService_DeleteEmail_PreserveAuthMethodTests
{
    // Test scenarios:
    //   1. Delete unverified email → always allowed (not an auth method).
    //   2. Delete verified email when user has 2 verified emails + 0 logins → allowed (1 verified email remains).
    //   3. Delete verified email when user has 1 verified email + 1 OAuth login → allowed (OAuth login remains).
    //   4. Delete verified email when user has 1 verified email + 0 logins → BLOCKED.
    //   5. Delete the IsOAuth-flagged email when AspNetUserLogins still has the row → allowed (login row independent of UserEmail).
    //   6. Delete the IsOAuth-flagged email when AspNetUserLogins empty + no other verified emails → BLOCKED.

    // [Test bodies — write each scenario with NSubstitute mocks of
    //   IUserEmailRepository, IUserService, UserManager<User>, IClock,
    //   IFullProfileInvalidator, IServiceProvider — covering the 6 cases above.
    //   Each test asserts that ValidationException is thrown (cases 4, 6) or
    //   that RemoveAsync is invoked on the repository (cases 1, 2, 3, 5).]

    // [The test file body in the actual implementation should expand each case
    //   into a discrete [HumansFact] method following the AAA pattern. Use
    //   _userManager.GetLoginsAsync(user) returning a UserLoginInfo list of
    //   appropriate length. Mock IUserEmailRepository.GetByUserIdForMutationAsync
    //   to return the relevant set of rows.]

    // [Helper: BuildSubject(...) returning a configured UserEmailService instance.]
}
```

> **Note:** the test file above is sketched — implement each of the 6 numbered scenarios as a discrete `[HumansFact]` method following the existing test conventions in `tests/Humans.Application.Tests/Services/Profile/`. Look at any existing `*Tests.cs` file in that folder for the exact NSubstitute setup pattern (`Substitute.For<IUserEmailRepository>()` etc.). Stick to the assertion shape `await act.Should().ThrowAsync<ValidationException>()` for the BLOCKED cases.

- [ ] **Step 2: Run the new tests, verify they FAIL**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~UserEmailService_DeleteEmail_PreserveAuthMethodTests"`
Expected: FAIL — current code blocks all `IsOAuth=true` deletes regardless of remaining auth methods.

- [ ] **Step 3: Implement the new guard**

Replace `DeleteEmailAsync` in `src/Humans.Application/Services/Profile/UserEmailService.cs:245-273`:

```csharp
    public async Task DeleteEmailAsync(
        Guid userId, Guid emailId, CancellationToken cancellationToken = default)
    {
        var email = await _repository.GetByIdAndUserIdAsync(emailId, userId, cancellationToken)
            ?? throw new InvalidOperationException("Email not found.");

        // Preserve-at-least-one-auth-method invariant (replaces the old
        // IsOAuth-based block). Auth methods are:
        //   (a) verified UserEmail rows (used by magic-link sign-in)
        //   (b) AspNetUserLogins rows (used by OAuth sign-in)
        // We only enforce when removing a verified row, since unverified rows
        // aren't usable for sign-in and deleting one cannot reduce auth-method count.
        if (email.IsVerified)
        {
            var allEmails = await _repository.GetByUserIdForMutationAsync(userId, cancellationToken);
            var verifiedRemaining = allEmails.Count(e => e.IsVerified && e.Id != emailId);

            var user = await _userService.GetByIdAsync(userId, cancellationToken)
                ?? throw new InvalidOperationException("User not found.");
            var loginCount = (await _userManager.GetLoginsAsync(user)).Count;

            if (verifiedRemaining == 0 && loginCount == 0)
            {
                throw new ValidationException(
                    "Cannot remove your last sign-in method. Add another verified email or link an OAuth provider first.");
            }

            // If this was the notification target, hand off to the next-best email.
            // Prefer another verified row; fall back to nothing (the user can re-pick
            // after the delete completes).
            if (email.IsNotificationTarget)
            {
                var successor = allEmails
                    .Where(e => e.Id != emailId && e.IsVerified)
                    .OrderByDescending(e => e.IsOAuth)
                    .ThenBy(e => e.DisplayOrder)
                    .FirstOrDefault();
                if (successor is not null)
                {
                    successor.IsNotificationTarget = true;
                    successor.UpdatedAt = _clock.GetCurrentInstant();
                    await _repository.UpdateAsync(successor, cancellationToken);
                }
            }
        }

        await _repository.RemoveAsync(email, cancellationToken);

        // FullProfile.NotificationEmail derives from user_emails; drop the stale entry so
        // admin/search/profile surfaces stop showing the removed address.
        await _fullProfileInvalidator.InvalidateAsync(userId, cancellationToken);
    }
```

> **Notable changes vs. existing code:**
> - The early `if (email.IsOAuth) throw ...` is removed.
> - The notification-target reassignment now prefers an OAuth-flagged row (was hard-coded to OAuth before) but generalizes to any verified row.
> - We block before mutating, not after.

- [ ] **Step 4: Run the new tests, verify they PASS**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~UserEmailService_DeleteEmail_PreserveAuthMethodTests"`
Expected: PASS (all 6 scenarios).

- [ ] **Step 5: Run all UserEmailService tests**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~UserEmailService"`
Expected: PASS. Existing tests that asserted `IsOAuth` rows can't be deleted should be updated — change them to assert "the OAuth row CAN be deleted as long as another auth method remains" or remove them as obsolete.

- [ ] **Step 6: Update `IUserEmailService.DeleteEmailAsync` XML doc**

In `src/Humans.Application/Interfaces/Profiles/IUserEmailService.cs:67-73`, replace:

```csharp
    /// <summary>
    /// Deletes a non-OAuth email address.
    /// </summary>
    Task DeleteEmailAsync(...
```

With:

```csharp
    /// <summary>
    /// Deletes an email address. Blocks the delete only if it would leave the
    /// user with zero verified UserEmail rows AND zero AspNetUserLogins rows
    /// (the "preserve at least one auth method" invariant). The OAuth-tied
    /// row (currently flagged via <see cref="UserEmail.IsOAuth"/>) is now
    /// deletable as long as another auth method remains.
    /// </summary>
    Task DeleteEmailAsync(...
```

- [ ] **Step 7: Commit**

```bash
git add src/Humans.Application/Services/Profile/UserEmailService.cs src/Humans.Application/Interfaces/Profiles/IUserEmailService.cs tests/Humans.Application.Tests/Services/Profile/UserEmailService_DeleteEmail_PreserveAuthMethodTests.cs
git commit -m "refactor(user-email): replace IsOAuth deletion block with preserve-auth-method invariant"
```

---

## Task 9: Set `RequireUniqueEmail = false`

**Files:**
- Modify: `src/Humans.Web/Program.cs:169`

- [ ] **Step 1: Edit the Identity options block**

Replace:

```csharp
        options.User.RequireUniqueEmail = true;
```

With:

```csharp
        // Email uniqueness now enforced at the UserEmails layer
        // (UserEmailService.AddEmailAsync + cross-User merge detection).
        // Identity-level uniqueness is incompatible with PR 1's stop-writes
        // approach (the column is null for new users) and would produce
        // spurious unique-violation failures on CreateAsync for any user
        // created after PR 1 ships.
        options.User.RequireUniqueEmail = false;
```

- [ ] **Step 2: Run the full test suite**

Run: `dotnet test Humans.slnx -v quiet`
Expected: all tests pass. If any test relied on `RequireUniqueEmail = true` to reject duplicate emails, update it to use the UserEmail-layer check.

- [ ] **Step 3: Commit**

```bash
git add src/Humans.Web/Program.cs
git commit -m "config(identity): RequireUniqueEmail = false (enforced at UserEmail layer)"
```

---

## Task 10: Admin backfill button — service layer (TDD)

**Files:**
- Create: `src/Humans.Application/Interfaces/Users/IUserEmailBackfillService.cs`
- Create: `src/Humans.Application/Services/Users/UserEmailBackfillService.cs`
- Create: `tests/Humans.Application.Tests/Services/Users/UserEmailBackfillServiceTests.cs`

- [ ] **Step 1: Write the interface**

Create `src/Humans.Application/Interfaces/Users/IUserEmailBackfillService.cs`:

```csharp
namespace Humans.Application.Interfaces.Users;

/// <summary>
/// One-shot administrative operation that creates a <see cref="Domain.Entities.UserEmail"/>
/// row for every <see cref="Domain.Entities.User"/> that has none. Idempotent:
/// re-running after a successful run is a no-op (returns count = 0).
///
/// Introduced in PR 1 of the email-identity-decoupling spec
/// (docs/superpowers/specs/2026-04-27-email-and-oauth-decoupling-design.md).
/// The PR 2 migration also runs an idempotent defensive backfill before the
/// column drop, so this button is the preferred pre-deploy path but not the
/// last line of defence.
/// </summary>
public interface IUserEmailBackfillService
{
    /// <summary>
    /// Backfills missing UserEmail rows from User.Email / User.EmailConfirmed.
    /// Returns a result containing how many rows were inserted and how many
    /// orphan Users (if any) had no User.Email to backfill from — those are
    /// flagged for admin review.
    /// </summary>
    Task<UserEmailBackfillResult> BackfillAsync(CancellationToken ct = default);
}

public record UserEmailBackfillResult(
    int OrphansFound,
    int RowsInserted,
    IReadOnlyList<Guid> SkippedUserIds);
```

- [ ] **Step 2: Write failing tests**

Create `tests/Humans.Application.Tests/Services/Users/UserEmailBackfillServiceTests.cs`. Implement these scenarios as `[HumansFact]` methods following existing test conventions in `tests/Humans.Application.Tests/Services/Users/`:

```
1. NoOrphans_ReturnsZeroCounts
   - Setup: every User has at least one UserEmail row
   - Assert: OrphansFound = 0, RowsInserted = 0, SkippedUserIds.Count = 0

2. OneOrphanWithEmail_InsertsOneRow
   - Setup: 1 User with Email="x@y.com" + EmailConfirmed=true, 0 UserEmail rows
   - Assert: OrphansFound=1, RowsInserted=1, the row has Email="x@y.com", IsVerified=true,
     IsNotificationTarget=true, IsOAuth=false (or true if a corresponding
     AspNetUserLogins exists — see scenario 4), CreatedAt/UpdatedAt set from clock
   - Verify audit log called with AuditAction.* (pick existing matching action)

3. OneOrphanWithoutEmail_RecordsSkip
   - Setup: 1 User with Email=null, 0 UserEmail rows
   - Assert: OrphansFound=1, RowsInserted=0, SkippedUserIds contains that user's Id

4. OrphanWithOAuthLogin_SetsIsOAuthTrue
   - Setup: 1 orphan User with Email="x@y.com" + 1 AspNetUserLogins row
   - Assert: inserted UserEmail row has IsOAuth=true

5. Idempotent_SecondRunIsNoop
   - Setup: 1 orphan User; first call inserts; second call finds 0 orphans
   - Assert: first call RowsInserted=1, second call RowsInserted=0

6. UnverifiedEmail_PreservesIsVerifiedFlag
   - Setup: 1 orphan User with Email="x@y.com", EmailConfirmed=false
   - Assert: inserted UserEmail row has IsVerified=false, IsNotificationTarget=false (unverified rows can't be notification target)
```

Use NSubstitute for `IUserRepository`, `IUserEmailRepository`, `IAuditLogService`, `UserManager<User>` (via `Microsoft.AspNetCore.Identity.UserManagerStub` pattern — copy from existing test utilities; if none exists, mock the minimal subset of methods used: `GetLoginsAsync`).

- [ ] **Step 3: Run tests, verify FAIL**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~UserEmailBackfillServiceTests"`
Expected: FAIL with "type or namespace 'UserEmailBackfillService' could not be found".

- [ ] **Step 4: Implement the service**

Create `src/Humans.Application/Services/Users/UserEmailBackfillService.cs`:

```csharp
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using NodaTime;

namespace Humans.Application.Services.Users;

/// <summary>
/// Implementation of <see cref="IUserEmailBackfillService"/>. See interface
/// XML doc for context.
/// </summary>
public sealed class UserEmailBackfillService : IUserEmailBackfillService
{
    private readonly IUserRepository _userRepository;
    private readonly IUserEmailRepository _userEmailRepository;
    private readonly UserManager<User> _userManager;
    private readonly IAuditLogService _auditLogService;
    private readonly IClock _clock;
    private readonly ILogger<UserEmailBackfillService> _logger;

    public UserEmailBackfillService(
        IUserRepository userRepository,
        IUserEmailRepository userEmailRepository,
        UserManager<User> userManager,
        IAuditLogService auditLogService,
        IClock clock,
        ILogger<UserEmailBackfillService> logger)
    {
        _userRepository = userRepository;
        _userEmailRepository = userEmailRepository;
        _userManager = userManager;
        _auditLogService = auditLogService;
        _clock = clock;
        _logger = logger;
    }

    public async Task<UserEmailBackfillResult> BackfillAsync(CancellationToken ct = default)
    {
        var orphans = await _userRepository.GetUsersWithoutUserEmailRowAsync(ct);
        var rowsInserted = 0;
        var skipped = new List<Guid>();
        var now = _clock.GetCurrentInstant();

        foreach (var user in orphans)
        {
            if (string.IsNullOrWhiteSpace(user.Email))
            {
                skipped.Add(user.Id);
                _logger.LogWarning(
                    "UserEmailBackfill: user {UserId} has no User.Email to backfill from — skipping",
                    user.Id);
                continue;
            }

            var hasOAuthLogin = (await _userManager.GetLoginsAsync(user)).Any();
            var userEmail = new UserEmail
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                Email = user.Email,
                IsVerified = user.EmailConfirmed,
                IsOAuth = hasOAuthLogin,
                IsNotificationTarget = user.EmailConfirmed,
                Visibility = ContactFieldVisibility.BoardOnly,
                DisplayOrder = 0,
                CreatedAt = now,
                UpdatedAt = now,
            };

            await _userEmailRepository.AddAsync(userEmail, ct);
            rowsInserted++;

            await _auditLogService.LogAsync(
                AuditAction.ContactCreated,
                nameof(User), user.Id,
                $"Backfilled missing UserEmail row from User.Email = {user.Email}",
                nameof(UserEmailBackfillService));

            _logger.LogInformation(
                "UserEmailBackfill: inserted UserEmail for user {UserId} ({Email})",
                user.Id, user.Email);
        }

        _logger.LogInformation(
            "UserEmailBackfill: complete — orphansFound={OrphansFound}, rowsInserted={RowsInserted}, skipped={Skipped}",
            orphans.Count, rowsInserted, skipped.Count);

        return new UserEmailBackfillResult(orphans.Count, rowsInserted, skipped);
    }
}
```

- [ ] **Step 5: Add `IUserRepository.GetUsersWithoutUserEmailRowAsync`**

Update `src/Humans.Application/Interfaces/Repositories/IUserRepository.cs` — add:

```csharp
    /// <summary>
    /// Returns every <see cref="User"/> that has no <see cref="UserEmail"/> row.
    /// Used by <see cref="Users.IUserEmailBackfillService"/> to find orphan Users
    /// during the PR 1 backfill operation.
    /// </summary>
    Task<IReadOnlyList<User>> GetUsersWithoutUserEmailRowAsync(CancellationToken ct = default);
```

Implement in `src/Humans.Infrastructure/Repositories/Users/UserRepository.cs`:

```csharp
    public async Task<IReadOnlyList<User>> GetUsersWithoutUserEmailRowAsync(CancellationToken ct = default)
    {
        return await _db.Users
            .Where(u => !_db.UserEmails.Any(ue => ue.UserId == u.Id))
            .ToListAsync(ct);
    }
```

(use the `_db` field name actually used in `UserRepository.cs` — read the file to confirm; the name might differ.)

- [ ] **Step 6: Wire DI**

Find the section DI registration for the User section. (Likely `InfrastructureServiceCollectionExtensions.cs` or `src/Humans.Web/Extensions/SectionRegistration/UsersSectionRegistration.cs`.) Search:

```bash
grep -rn "AccountProvisioningService\|IAccountProvisioningService" src/Humans.Web/Extensions src/Humans.Infrastructure/Extensions 2>/dev/null
```

Add adjacent registration:

```csharp
services.AddScoped<IUserEmailBackfillService, UserEmailBackfillService>();
```

- [ ] **Step 7: Run tests, verify PASS**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~UserEmailBackfillServiceTests"`
Expected: all 6 scenarios PASS.

- [ ] **Step 8: Commit**

```bash
git add src/Humans.Application/Interfaces/Users/IUserEmailBackfillService.cs src/Humans.Application/Services/Users/UserEmailBackfillService.cs src/Humans.Application/Interfaces/Repositories/IUserRepository.cs src/Humans.Infrastructure/Repositories/Users/UserRepository.cs tests/Humans.Application.Tests/Services/Users/UserEmailBackfillServiceTests.cs
# also include the DI registration file in this commit
git commit -m "feat(users): add UserEmailBackfillService for orphan-User cleanup (PR 1 admin button)"
```

---

## Task 11: Admin backfill button — controller + view

**Files:**
- Investigate where existing admin-only Users actions live (e.g. `src/Humans.Web/Controllers/Admin/`).
- Either modify an existing admin controller or create `src/Humans.Web/Controllers/Admin/UsersAdminController.cs`.
- Create: `src/Humans.Web/Views/Admin/Users/BackfillUserEmails.cshtml`

- [ ] **Step 1: Reconnaissance — find an existing admin controller pattern to follow**

Run: `find src/Humans.Web -name "*Admin*Controller.cs" -type f`
Read one or two of the resulting controllers to understand:
  - The `[Authorize(Roles = ...)]` attribute used (probably `RoleNames.Admin`)
  - The route prefix pattern
  - The localization-resource convention (resx files referenced)
  - The view path convention

Pick the most appropriate place to add the action — preferably an existing controller that already groups admin user-related actions; otherwise a new `UsersAdminController` under `/Admin/Users`.

- [ ] **Step 2: Add the GET action (confirm page)**

Add to the chosen controller:

```csharp
    /// <summary>
    /// PR 1 of email-identity-decoupling spec: one-shot operator-triggered
    /// backfill of UserEmail rows for any orphan Users. Idempotent — safe to
    /// re-run. Recommended before PR 2 deploys so any humans needing manual
    /// triage (no User.Email) are surfaced ahead of the column-drop migration;
    /// the migration itself also runs a defensive INSERT-WHERE-NOT-EXISTS.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> BackfillUserEmails(CancellationToken ct)
    {
        var orphans = await _userEmailBackfillService.PreviewAsync(ct);
        return View(new BackfillUserEmailsViewModel(OrphansFound: orphans.Count, RowsInserted: null, Skipped: null));
    }

    [HttpPost, ValidateAntiForgeryToken]
    public async Task<IActionResult> RunBackfillUserEmails(CancellationToken ct)
    {
        var result = await _userEmailBackfillService.BackfillAsync(ct);
        return View(nameof(BackfillUserEmails),
            new BackfillUserEmailsViewModel(
                OrphansFound: result.OrphansFound,
                RowsInserted: result.RowsInserted,
                Skipped: result.SkippedUserIds));
    }
```

> **Note:** if you want a preview-without-mutation step, add `Task<IReadOnlyList<Guid>> PreviewAsync(CancellationToken ct)` to `IUserEmailBackfillService` that calls `GetUsersWithoutUserEmailRowAsync` and returns the list of orphan IDs. Otherwise, simplify to a single GET that runs and shows the result (idempotent — safe).

For the simpler single-GET design, replace the two methods above with:

```csharp
    [HttpGet, HttpPost]
    [ValidateAntiForgeryToken(IfHttpVerbs = "POST")] // (use whichever attr is conventional)
    public async Task<IActionResult> BackfillUserEmails(CancellationToken ct)
    {
        if (HttpContext.Request.Method == "POST")
        {
            var result = await _userEmailBackfillService.BackfillAsync(ct);
            return View(new BackfillUserEmailsViewModel(result.OrphansFound, result.RowsInserted, result.SkippedUserIds));
        }
        return View(new BackfillUserEmailsViewModel(0, null, null));
    }
```

Pick whichever fits the existing controller conventions. **Match the surrounding code style.**

- [ ] **Step 3: Add the view model**

Add to the controller file (or `Models/Admin/`):

```csharp
public record BackfillUserEmailsViewModel(
    int OrphansFound,
    int? RowsInserted,
    IReadOnlyList<Guid>? Skipped);
```

- [ ] **Step 4: Add the view**

Create `src/Humans.Web/Views/Admin/Users/BackfillUserEmails.cshtml`:

```cshtml
@model BackfillUserEmailsViewModel
@{
    ViewData["Title"] = "Backfill UserEmail rows";
}

<h1>Backfill UserEmail rows</h1>

<p>
    Inserts a <code>UserEmail</code> row for any humans that have <code>User.Email</code>
    but no corresponding row in <code>user_emails</code>. Idempotent — safe to run repeatedly.
</p>

<p>
    This step is required before deploying PR 2 of the email-identity-decoupling
    work. The PR 2 migration also runs a defensive INSERT-WHERE-NOT-EXISTS
    before dropping the columns, so this button is the preferred pre-deploy
    path but not the last line of defence.
</p>

@if (Model.RowsInserted is not null)
{
    <div class="alert alert-success">
        <strong>Done.</strong>
        Orphans found: @Model.OrphansFound. Rows inserted: @Model.RowsInserted.
        @if (Model.Skipped is { Count: > 0 })
        {
            <details>
                <summary>@Model.Skipped.Count user(s) skipped (no <code>User.Email</code> to backfill from)</summary>
                <ul>
                    @foreach (var id in Model.Skipped) { <li>@id</li> }
                </ul>
                <p>These users need manual admin review — they have no usable email anchor.</p>
            </details>
        }
    </div>
}

<form method="post" asp-action="BackfillUserEmails">
    @Html.AntiForgeryToken()
    <button type="submit" class="btn btn-primary">Run backfill</button>
</form>
```

> **Style:** match the framing of other admin one-shot views in the repo. Look at any existing similar admin tool view for the exact CSS classes, layout, and localization pattern. Adjust this template to fit.

- [ ] **Step 5: Add a nav link**

Per CLAUDE.md "Every new page MUST have a nav link." Find the admin navigation source. Most likely `src/Humans.Web/Components/AdminSidebarViewComponent.cs` or `src/Humans.Web/Configuration/AdminNavigation.cs` (recent admin-shell work — see commit `cf5216b8`). Add an entry under a maintenance/utility group pointing at the new action.

- [ ] **Step 6: Build + smoke**

Run: `dotnet build Humans.slnx -v quiet`
Expected: success.

Optional manual: `dotnet run --project src/Humans.Web` and load `/Admin/Users/BackfillUserEmails`. Confirm page renders with no orphan count on a fresh DB.

- [ ] **Step 7: Commit**

```bash
git add src/Humans.Web/Controllers/Admin src/Humans.Web/Views/Admin/Users/BackfillUserEmails.cshtml src/Humans.Web/Components/AdminSidebarViewComponent.cs # or whichever nav file you edited
git commit -m "feat(admin): add Backfill UserEmail rows admin action (PR 1 backfill button)"
```

---

## Task 12: Final architecture-test verification

- [ ] **Step 1: Run the architecture test**

Run: `dotnet test Humans.slnx -v quiet --filter "FullyQualifiedName~IdentityColumnWriteRestrictionsTests"`
Expected: PASS — no offenders in Application or Web.

- [ ] **Step 2: Run the full test suite**

Run: `dotnet test Humans.slnx -v quiet`
Expected: all green. If any test fails because it was relying on `User.Email` being populated post-creation, update it to assert via UserEmail rows.

- [ ] **Step 3: Build with warnings as errors locally**

Run: `dotnet build Humans.slnx -v quiet`
Expected: success, no new warnings.

- [ ] **Step 4: If anything failed in steps 1–3, fix and re-run before proceeding.**

---

## Task 13: Manual smoke (browser)

Per CLAUDE.md: UI/auth changes need browser verification, not just type-checking + tests.

- [ ] **Step 1: Start the dev server**

```bash
dotnet run --project src/Humans.Web
```

- [ ] **Step 2: Smoke each path on http://nuc.home (or localhost — Peter uses nuc.home for dev)**

For each path, confirm: (a) sign-in succeeds, (b) the User row exists, (c) a UserEmail row exists, (d) the four Identity columns on the new User are null/empty as expected.

  - [ ] **Magic-link signup (new user):** go to `/Account/Login`, request magic link with a never-seen email, click the link, complete signup. Verify in DB: `SELECT Email, NormalizedEmail, EmailConfirmed, UserName, NormalizedUserName FROM Users WHERE Id = '<new-id>'` returns nulls/false; `UserEmails` has the row.
  - [ ] **Magic-link sign-in (existing user):** sign out, request magic link with the email used above, click link. Verify sign-in succeeds.
  - [ ] **Google OAuth (new user — preview env or staging only, not local):** if testable; otherwise note as "deferred to QA verification."
  - [ ] **Dev login persona seed (clean DB):** `/dev/login/admin`. Verify dev persona created, signed in.
  - [ ] **Dev login legacy persona (DB with pre-PR-1 personas):** clone QA DB locally; `/dev/login/admin` should reuse the existing persona via `FindByIdAsync`.
  - [ ] **Email add + delete (existing multi-email user):** add a second email, verify, then delete the OAuth-flagged one. Verify it actually deletes (was previously blocked).
  - [ ] **Email add + delete last auth method:** create a fresh user, try to delete their only verified email. Verify ValidationException ("Cannot remove your last sign-in method...").
  - [ ] **Admin backfill button (clean DB, no orphans):** `/Admin/Users/BackfillUserEmails`. Click. Expect "Orphans found: 0".
  - [ ] **Admin backfill button (DB with one orphan):** manually delete a UserEmail row in DB; click backfill; verify the row is recreated. Click again; verify second click reports zero.

- [ ] **Step 3: If any smoke fails, fix and re-verify before declaring done.**

---

## Task 14: Push and open PR

- [ ] **Step 1: Verify clean working tree (all commits made)**

Run: `git status`
Expected: clean.

- [ ] **Step 2: Push branch**

Run: `git push -u origin email-oauth-pr1-decouple-writes`

- [ ] **Step 3: Open PR**

Run:

```bash
gh pr create --repo peterdrier/Humans \
  --base main \
  --title "Email-identity decoupling PR 1 — stop Identity-column writes + admin backfill button" \
  --body "$(cat <<'EOF'
Implements PR 1 of the email-identity-decoupling spec
(`docs/superpowers/specs/2026-04-27-email-and-oauth-decoupling-design.md`,
revised 2026-04-28).

## Summary

- Stop writing `Email` / `NormalizedEmail` / `EmailConfirmed` / `UserName` on User-creation paths in the OAuth callback, magic-link signup, account provisioning, and dev-login persona seeding. UserName becomes `id.ToString()`; the email columns stay null. UserEmail rows are now the source of truth.
- Restructure `AccountController.ExternalLoginCallback` new-User branch to delete the just-created User if `AddLoginAsync` fails — prevents orphan Users with no auth method now that `RequireUniqueEmail = false` removes the existing safety.
- Replace the `IsOAuth`-based deletion guard in `UserEmailService.DeleteEmailAsync` with the "preserve at least one auth method" invariant (block only if zero verified UserEmails AND zero AspNetUserLogins remain).
- Add `/Admin/Users/BackfillUserEmails` admin one-shot action — idempotent backfill of UserEmail rows for any orphan Users. Recommended before PR 2's column-drop migration so any humans needing manual triage are flagged ahead of time; the PR 2 migration also runs a defensive INSERT-WHERE-NOT-EXISTS so missed orphans are repaired before the drop.
- Architecture test forbidding writes to the four Identity columns in Application + Web (Infrastructure exempt — UserRepository's anonymization writes become no-ops in PR 2; DevLoginController exempt for transitional `UserName = id.ToString()`).
- Set `RequireUniqueEmail = false` (uniqueness now enforced at UserEmail layer).

## What this PR does NOT do (deferred to PR 2)

- Read sweep: all `_userManager.FindByEmailAsync` / `IUserRepository.GetByNormalizedEmailAsync` paths stay as-is. They're defensive fallbacks for legacy Users with stale `User.Email` and (now) missing UserEmail rows. Removing them in PR 1 would lock those users out; PR 2 sweeps reads after the backfill button has run.
- `AccountController.ExternalLoginCallback` lockout-relink branch and `FindUserByAnyEmailAsync` branch both stay — they're defensive paths that PR 2 removes alongside the read sweep.
- `UserRepository` anonymization writes (rename / merge / purge / deletion) — these stay through PR 1 and become no-ops in PR 2 when the columns drop.
- `User.Email` virtual override + `HumansUserStore` + column drop — entire scope of PR 2.

## Test plan

- [ ] `dotnet build Humans.slnx -v quiet` — clean
- [ ] `dotnet test Humans.slnx -v quiet` — all green
- [ ] Architecture test `IdentityColumnWriteRestrictionsTests.NoApplicationOrWebCode_WritesIdentityEmailColumnsOnUser` passes
- [ ] Browser smoke: magic-link signup (new + existing user)
- [ ] Browser smoke: dev-login persona seed (clean + legacy DB)
- [ ] Browser smoke: email add + delete on multi-email user
- [ ] Browser smoke: email delete blocks last auth method
- [ ] Admin smoke: `/Admin/Users/BackfillUserEmails` returns 0 orphans on clean DB; recreates a row when one is deleted

## Continuity / rollback

- No schema change. Revert deploy = full rollback.
- Lockout-relink and `FindUserByAnyEmailAsync` fallback branches preserved — legacy Users remain reachable.
- Backfill button is idempotent — safe to run on any environment any number of times.

## Linked

- Spec revision in this PR: `docs/superpowers/specs/2026-04-27-email-and-oauth-decoupling-design.md`
- Plan: `docs/superpowers/plans/2026-04-28-email-oauth-pr1-decouple-writes.md`
- Issue: TBD (link added when issue exists)
EOF
)"
```

- [ ] **Step 4: Wait for CI green + fix any review findings before declaring done.**

(Per memory: "Done means Codex-clean, not just pushed." Wait for the auto-review pass; address any findings as a follow-up commit on the same branch.)

---

## Self-Review Checklist

After implementing all tasks above:

1. **Spec coverage:** Each PR-1 bullet from the revised spec maps to a task above:
   - Stop writes (5 sites) → Tasks 4, 5, 6, 7
   - OAuth callback restructure → Task 5 (bundled with the Stop-writes change)
   - Deletion guard → Task 8
   - Admin backfill button → Tasks 10, 11
   - `RequireUniqueEmail = false` → Task 9
   - Architecture test → Task 3
   - Lockout-relink + FindUserByAnyEmailAsync stay → no-op (verified by reading the code, not editing)
   - Anonymization writes stay → no-op (verified by reading UserRepository, not editing)

2. **Placeholder scan:** Tasks 8 and 10 sketch test bodies as "implement these scenarios" — that's intentional because the existing test conventions (NSubstitute setup, `[HumansFact]`, etc.) need to match this repo's existing pattern; an agent should read one neighbor test before writing the new one. Every code-changing step has full code shown.

3. **Type consistency:** `IUserEmailBackfillService` has `BackfillAsync` (Task 10 step 1) and `PreviewAsync` (Task 11 step 2) — Task 11 step 2 will need to add `PreviewAsync` to the interface or simplify the controller to single-GET (the alternative is also presented). Decide at Task 11 time.

4. **PR 2 scope reminder:** the column-drop migration in PR 2 must include the defensive `INSERT … WHERE NOT EXISTS` before the `DROP COLUMN`. The admin button added in this PR is the preferred operator path; the migration's defensive INSERT is the last line of defence.
