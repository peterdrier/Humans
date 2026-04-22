using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;
using UserService = Humans.Application.Services.Users.UserService;
using AccountProvisioningService = Humans.Application.Services.Users.AccountProvisioningService;
using UnsubscribeService = Humans.Application.Services.Users.UnsubscribeService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 repository pattern for the User
/// section — migrated per PR #243 / issue #511.
///
/// <para>
/// The User section's §15 migration chose <b>Option A</b> (no caching
/// decorator, no dict cache, no DTO) per
/// <c>docs/superpowers/specs/2026-04-21-issue-511-user-migration.md</c>.
/// User is ~500 rows with no stitched projection or hot bulk-read path, so
/// a decorator is not warranted — same rationale Governance used when its
/// decorator was removed in #242. These tests enforce the non-decorator shape:
/// UserService lives in Application, goes through IUserRepository, and wires
/// IFullProfileInvalidator for cross-section cache-staleness signalling.
/// </para>
/// </summary>
public class UserArchitectureTests
{
    // ── UserService ──────────────────────────────────────────────────────────

    [Fact]
    public void UserService_LivesInHumansApplicationServicesUsersNamespace()
    {
        typeof(UserService).Namespace
            .Should().Be("Humans.Application.Services.Users",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [Fact]
    public void UserService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(UserService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — use IUserRepository instead (design-rules §3)");
    }

    [Fact]
    public void UserService_HasNoIMemoryCacheConstructorParameter()
    {
        var ctor = typeof(UserService).GetConstructors().Single();
        var cachingParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Microsoft.Extensions.Caching.Memory", StringComparison.Ordinal));

        cachingParam.Should().BeNull(
            because: "canonical User data is not IMemoryCache-backed; cross-section invalidation goes through IFullProfileInvalidator (design-rules §5)");
    }

    [Fact]
    public void UserService_TakesRepository()
    {
        var ctor = typeof(UserService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IUserRepository));
    }

    [Fact]
    public void UserService_TakesFullProfileInvalidator()
    {
        var ctor = typeof(UserService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IFullProfileInvalidator),
            because: "UserService writes that change FullProfile-visible fields (DisplayName, GoogleEmail) must invalidate the Profile cache; the dependency proves the wire is in place so cache-staleness regressions fail at compile/test time rather than silently at runtime");
    }

    [Fact]
    public void UserService_ConstructorTakesNoStoreType()
    {
        var ctor = typeof(UserService).GetConstructors().Single();
        var storeParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Humans.Application.Interfaces.Stores", StringComparison.Ordinal));

        storeParam.Should().BeNull(
            because: "Application services must not depend on store abstractions (design-rules §15); the User section's §15 migration went further and does not use a store at all");
    }

    // ── IUserRepository ──────────────────────────────────────────────────────

    [Fact]
    public void IUserRepository_LivesInApplicationInterfacesRepositoriesNamespace()
    {
        typeof(IUserRepository).Namespace
            .Should().Be("Humans.Application.Interfaces.Repositories",
                because: "repository interfaces live in Humans.Application.Interfaces.Repositories per design-rules §3");
    }

    [Fact]
    public void UserRepository_IsSealed()
    {
        // Mirrors ProfileRepository — repository implementations are terminal; no subclass should
        // extend or override the EF-backed data access.
        var repoType = typeof(IUserRepository).Assembly
            .GetExportedTypes()
            .Concat(typeof(Humans.Infrastructure.Repositories.UserRepository).Assembly.GetExportedTypes())
            .Single(t => string.Equals(t.Name, "UserRepository", StringComparison.Ordinal)
                         && typeof(IUserRepository).IsAssignableFrom(t));

        repoType.IsSealed.Should().BeTrue(
            because: "repository implementations are sealed to prevent ad-hoc extension; any new behavior belongs on the interface");
    }

    // ── AccountProvisioningService ───────────────────────────────────────────

    [Fact]
    public void AccountProvisioningService_LivesInHumansApplicationServicesUsersNamespace()
    {
        typeof(AccountProvisioningService).Namespace
            .Should().Be("Humans.Application.Services.Users",
                because: "AccountProvisioningService is part of the User section (issue #558) and must live with UserService in Application (design-rules §15i)");
    }

    [Fact]
    public void AccountProvisioningService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(AccountProvisioningService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — use IUserRepository / IUserEmailRepository (design-rules §3)");
    }

    [Fact]
    public void AccountProvisioningService_TakesRepositoryAndUserEmailRepository()
    {
        var ctor = typeof(AccountProvisioningService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IUserRepository));
        paramTypes.Should().Contain(typeof(IUserEmailRepository));
    }

    // ── UnsubscribeService ───────────────────────────────────────────────────

    [Fact]
    public void UnsubscribeService_LivesInHumansApplicationServicesUsersNamespace()
    {
        typeof(UnsubscribeService).Namespace
            .Should().Be("Humans.Application.Services.Users",
                because: "UnsubscribeService operates on User state and is part of the User section (issue #558, design-rules §8 — Unsubscribe row)");
    }

    [Fact]
    public void UnsubscribeService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(UnsubscribeService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — use IUserRepository (design-rules §3)");
    }

    [Fact]
    public void UnsubscribeService_TakesRepository()
    {
        var ctor = typeof(UnsubscribeService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IUserRepository));
    }
}
