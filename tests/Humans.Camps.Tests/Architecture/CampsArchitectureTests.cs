using Humans.Gdpr.Contracts;
using Humans.GoogleIntegration.Contracts;
using AwesomeAssertions;
using Microsoft.Extensions.Configuration;
using Humans.Base.Caching;
using Humans.Base.Enums;
using Humans.Settings.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using NSubstitute;
using CampRoleService = Humans.Camps.Services.CampRoleService;
using CampService = Humans.Camps.Services.CampService;

namespace Humans.Camps.Tests.Architecture;

/// <summary>
/// Architecture tests for the Camps section — §15 caching decorator. Pins
/// that <c>CachingCampService</c> wraps <c>CampService</c> with a Singleton, hit-tracked
/// per-camp projection plus a separate CampSettingsInfo slot.
/// </summary>
public class CampsArchitectureTests
{
    // ── CachingCampService decorator ──────────────────────────────────

    [HumansFact]
    public void CachingCampService_ImplementsICampManagement()
    {
        typeof(ICampManagement).IsAssignableFrom(typeof(CachingCampService))
            .Should().BeTrue(
                because: "the decorator transparently substitutes the inner service per §15a");
    }

    [HumansFact]
    public void CachingCampService_CarriesTheWritesThatReshapeItsProjection()
    {
        typeof(ICampRoleService).IsAssignableFrom(typeof(CachingCampService))
            .Should().BeTrue(
                because: "role writes reshape the cached role projection, so the cache owner carries them and evicts after the inner write");
        typeof(IUserDataContributor).IsAssignableFrom(typeof(CachingCampService))
            .Should().BeTrue(
                because: "GDPR erasure empties rosters the projection serves, so the cache owner carries it and evicts after the inner erase");
    }

    [HumansFact]
    public void CachingCampService_ExtendsTrackedCacheKeyedByCampId()
    {
        typeof(CachingCampService).BaseType
            .Should().Be(typeof(TrackedCache<Guid, CampInfo>),
                because: "the canonical Camps read-model is keyed by camp id; sub-views (year filters) project from this canonical cache rather than holding their own keys");
    }

    [HumansFact]
    public void CachingCampService_LivesInInfrastructureServicesCampsNamespace()
    {
        typeof(CachingCampService).Namespace
            .Should().Be("Humans.Camps.Services",
                because: "§15d decorators live in Humans.Infrastructure.Services.<Section>");
    }

    // ── Google Group membership source — Camps claim ─────────────────────────

    /// <summary>
    /// Issue nobodies-collective/Humans#740: CampRoleService is the only
    /// Camps-side <see cref="IGoogleGroupMembershipSource"/> claimant. Pins
    /// the rule that any new Camps source goes through this service so the
    /// orchestrator's collision detection sees a single Camps voice per group key.
    /// </summary>
    [HumansFact]
    public void CampRoleService_IsTheOnlyCampsSideGoogleGroupMembershipSource()
    {
        var campsAssembly = typeof(CampService).Assembly;
        var campsClaimants = campsAssembly
            .GetTypes()
            .Where(t => !t.IsAbstract
                        && !t.IsInterface
                        && typeof(IGoogleGroupMembershipSource).IsAssignableFrom(t)
                        && (t.Namespace ?? string.Empty).StartsWith(
                            "Humans.Camps.Services", StringComparison.Ordinal))
            .Select(t => t.FullName ?? t.Name)
            .ToList();

        campsClaimants.Should().BeEquivalentTo([typeof(CampRoleService).FullName!],
            because: "CampRoleService is the only Camps-side IGoogleGroupMembershipSource claimant; new Camps groups must route through this service so the orchestrator's collision check sees one Camps voice per group key (issue nobodies-collective/Humans#740)");
    }

    [HumansFact]
    public void CampRoleService_DependsOnNarrowCampRoleAccess()
    {
        var ctor = typeof(CampRoleService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(ICampRoleCampAccess),
            because: "role workflows need only membership/status/settings helpers, not the whole ICampManagement surface");
        paramTypes.Should().NotContain(typeof(ICampManagement),
            because: "the role sub-service must not depend on the full Camps service surface");
    }

    [HumansFact]
    public void CachingCampService_ImplementsICampRoleCampAccess()
    {
        typeof(ICampRoleCampAccess).IsAssignableFrom(typeof(CachingCampService))
            .Should().BeTrue(
                because: "CampRoleService must use the decorator-backed port so migration writes still invalidate CampInfo");
    }

    [HumansFact]
    public void CampInfo_Active_ReturnsLatestSeasonByYear()
    {
        // Smoke test: CampInfo.Active picks the highest-year season.
        var season2024 = new CampSeasonInfo(Guid.NewGuid(), Guid.NewGuid(), "slug", 2024, null,
            "Camp 2024", string.Empty, string.Empty, [], CampSeasonStatus.Pending,
            YesNoMaybe.No, YesNoMaybe.No, AdultPlayspacePolicy.No,
            0, null, null, null, 0, null, null);
        var season2026 = new CampSeasonInfo(Guid.NewGuid(), Guid.NewGuid(), "slug", 2026, null,
            "Camp 2026", string.Empty, string.Empty, [], CampSeasonStatus.Pending,
            YesNoMaybe.No, YesNoMaybe.No, AdultPlayspacePolicy.No,
            0, null, null, null, 0, null, null);

        var camp = new CampInfo(Guid.NewGuid(), "slug", "email@example.com", "+34 600 000 000",
            false, 0, [season2024, season2026]);

        camp.Active.Should().Be(season2026,
            because: "Active returns the season with the highest Year");
        camp.Active!.Name.Should().Be("Camp 2026");
    }

    /// <summary>
    /// Asserts that <see cref="ICampManagement"/> and <see cref="ICampService"/> resolve to the same
    /// singleton from the production DI registration — <c>Section.Register</c>
    /// itself, not a test-built mirror of it.
    /// </summary>
    [HumansFact]
    public void ICampManagement_And_ICampService_ResolveToSameInstance()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Substitute.For<IClock>());
        new Section().Register(services, new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var fromManagement = scope.ServiceProvider.GetRequiredService<ICampManagement>();
        var fromPublic = scope.ServiceProvider.GetRequiredService<ICampService>();

        fromManagement.Should().BeOfType<CachingCampService>(
            because: "the cache decorator is the section's public face");
        ReferenceEquals(fromManagement, fromPublic).Should().BeTrue(
            because: "both interfaces must expose the same singleton so the cache is shared");
    }
}
