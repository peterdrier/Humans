using AwesomeAssertions;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Microsoft.EntityFrameworkCore;
using Xunit;
using MagicLinkService = Humans.Application.Services.Auth.MagicLinkService;
using RoleAssignmentService = Humans.Application.Services.Auth.RoleAssignmentService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 repository pattern for the Auth
/// section — migrated per issue #551. Auth is admin-only and low-traffic
/// (handful of role changes per month, rare magic-link sends), so no caching
/// decorator sits in front of either service — they go directly through
/// <see cref="IRoleAssignmentRepository"/> / cross-section service interfaces
/// and invalidate cross-cutting caches via
/// <see cref="INavBadgeCacheInvalidator"/> +
/// <see cref="IRoleAssignmentClaimsCacheInvalidator"/> after successful writes.
/// </summary>
public class AuthArchitectureTests
{
    // ── RoleAssignmentService ────────────────────────────────────────────────

    [Fact]
    public void RoleAssignmentService_LivesInHumansApplicationServicesAuthNamespace()
    {
        typeof(RoleAssignmentService).Namespace
            .Should().Be("Humans.Application.Services.Auth",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [Fact]
    public void RoleAssignmentService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(RoleAssignmentService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — use IRoleAssignmentRepository instead (design-rules §3)");
    }

    [Fact]
    public void RoleAssignmentService_HasNoIMemoryCacheConstructorParameter()
    {
        var ctor = typeof(RoleAssignmentService).GetConstructors().Single();
        var cachingParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Microsoft.Extensions.Caching.Memory", StringComparison.Ordinal));

        cachingParam.Should().BeNull(
            because: "Auth has no canonical domain cache; nav-badge and role-assignment-claims invalidation route through INavBadgeCacheInvalidator / IRoleAssignmentClaimsCacheInvalidator, not IMemoryCache directly (design-rules §5)");
    }

    [Fact]
    public void RoleAssignmentService_TakesRepository()
    {
        var ctor = typeof(RoleAssignmentService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IRoleAssignmentRepository));
    }

    [Fact]
    public void RoleAssignmentService_TakesUserServiceForNavStitching()
    {
        var ctor = typeof(RoleAssignmentService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IUserService),
            because: "RoleAssignmentService resolves assignee / assigner display names via IUserService.GetByIdsAsync and stitches them onto the [Obsolete] cross-domain navs in memory (design-rules §6b)");
    }

    [Fact]
    public void RoleAssignmentService_TakesClaimsInvalidator()
    {
        var ctor = typeof(RoleAssignmentService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IRoleAssignmentClaimsCacheInvalidator),
            because: "RoleAssignmentService must invalidate the per-user claims cache after Assign/End/Revoke — the dependency proves the wire is in place so the RoleAssignmentClaimsTransformation 60-second cached snapshot does not serve stale claims");
    }

    [Fact]
    public void RoleAssignmentService_TakesNavBadgeInvalidator()
    {
        var ctor = typeof(RoleAssignmentService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(INavBadgeCacheInvalidator),
            because: "Assign/End affect nav-badge counts (active role totals), so the service invalidates the nav-badge cache after writes");
    }

    [Fact]
    public void RoleAssignmentService_ConstructorTakesNoStoreType()
    {
        var ctor = typeof(RoleAssignmentService).GetConstructors().Single();
        var storeParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Humans.Application.Interfaces.Stores", StringComparison.Ordinal));

        storeParam.Should().BeNull(
            because: "Application services must not depend on store abstractions (design-rules §15); the Auth section has no store at all");
    }

    // ── IRoleAssignmentRepository ────────────────────────────────────────────

    [Fact]
    public void IRoleAssignmentRepository_LivesInApplicationInterfacesRepositoriesNamespace()
    {
        typeof(IRoleAssignmentRepository).Namespace
            .Should().Be("Humans.Application.Interfaces.Repositories",
                because: "repository interfaces live in Humans.Application.Interfaces.Repositories per design-rules §3");
    }

    [Fact]
    public void RoleAssignmentRepository_IsSealed()
    {
        var repoType = typeof(Humans.Infrastructure.Repositories.RoleAssignmentRepository);

        repoType.IsSealed.Should().BeTrue(
            because: "repository implementations are sealed to prevent ad-hoc extension; any new behavior belongs on the interface");
    }

    // ── MagicLinkService ─────────────────────────────────────────────────────

    [Fact]
    public void MagicLinkService_LivesInHumansApplicationServicesAuthNamespace()
    {
        typeof(MagicLinkService).Namespace
            .Should().Be("Humans.Application.Services.Auth",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [Fact]
    public void MagicLinkService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(MagicLinkService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "MagicLinkService owns no tables — verified-email lookup routes through IUserEmailService.FindVerifiedEmailWithUserAsync instead of direct DbContext queries");
    }

    [Fact]
    public void MagicLinkService_HasNoIMemoryCacheConstructorParameter()
    {
        var ctor = typeof(MagicLinkService).GetConstructors().Single();
        var cachingParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Microsoft.Extensions.Caching.Memory", StringComparison.Ordinal));

        cachingParam.Should().BeNull(
            because: "MagicLink's token-replay and signup-cooldown state routes through IMagicLinkRateLimiter, an Application-layer abstraction (same pattern as IUnsubscribeTokenProvider)");
    }

    [Fact]
    public void MagicLinkService_HasNoEmailSettingsOrDataProtectionConstructorParameter()
    {
        var ctor = typeof(MagicLinkService).GetConstructors().Single();
        var settingsParam = ctor.GetParameters()
            .FirstOrDefault(p =>
                (p.ParameterType.FullName ?? string.Empty)
                    .Contains("EmailSettings", StringComparison.Ordinal) ||
                (p.ParameterType.FullName ?? string.Empty)
                    .Contains("IDataProtectionProvider", StringComparison.Ordinal));

        settingsParam.Should().BeNull(
            because: "Data-protection and URL construction live behind IMagicLinkUrlBuilder in Infrastructure so MagicLinkService stays free of Infrastructure config (mirrors CommunicationPreferenceService + IUnsubscribeTokenProvider)");
    }

    [Fact]
    public void MagicLinkService_TakesUrlBuilderAndRateLimiter()
    {
        var ctor = typeof(MagicLinkService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IMagicLinkUrlBuilder));
        paramTypes.Should().Contain(typeof(IMagicLinkRateLimiter));
        paramTypes.Should().Contain(typeof(IUserEmailService),
            because: "verified-email lookup goes through IUserEmailService.FindVerifiedEmailWithUserAsync instead of a direct DbContext.UserEmails query");
    }
}
