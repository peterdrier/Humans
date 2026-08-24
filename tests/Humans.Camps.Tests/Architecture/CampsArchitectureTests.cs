using Humans.GoogleIntegration.Contracts;
using AwesomeAssertions;
using Humans.Base.Caching;
using Humans.Base.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using NSubstitute;
using CampRoleService = Humans.Camps.Services.CampRoleService;
using CampService = Humans.Camps.Services.CampService;

namespace Humans.Camps.Tests.Architecture;

/// <summary>
/// Architecture tests for the Camps section — §15 caching decorator (T-06, 2026-05-16). Pins
/// that <c>CachingCampService</c> wraps <c>CampService</c> with a Singleton, hit-tracked
/// per-camp projection plus a separate CampSettingsInfo slot.
/// </summary>
public class CampsArchitectureTests
{
    // ── CachingCampService (T-06 decorator) ──────────────────────────────────

    [HumansFact]
    public void CachingCampService_ImplementsICampService()
    {
        typeof(ICampService).IsAssignableFrom(typeof(CachingCampService))
            .Should().BeTrue(
                because: "the decorator transparently substitutes the inner service per §15a");
    }

    [HumansFact]
    public void CachingCampService_ImplementsICampInfoInvalidator()
    {
        typeof(ICampInfoInvalidator).IsAssignableFrom(typeof(CachingCampService))
            .Should().BeTrue(
                because: "the decorator is the cache and the signaller — every mutating method invalidates the affected camp inline after the inner write (§15e)");
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
            because: "role workflows need only membership/status/settings helpers, not the whole ICampService surface");
        paramTypes.Should().NotContain(typeof(ICampService),
            because: "the role sub-service must not depend on the full Camps service surface");
    }

    // ── ICampServiceRead split (memory/architecture/section-read-write-split.md) ──

    [HumansFact]
    public void ICampService_InheritsICampServiceRead()
    {
        typeof(ICampServiceRead).IsAssignableFrom(typeof(ICampService))
            .Should().BeTrue(
                because: "ICampService is the full Camps surface; external sections inject the narrow ICampServiceRead. " +
                         "See memory/architecture/section-read-write-split.md.");
    }

    [HumansFact]
    public void CachingCampService_ImplementsICampServiceRead()
    {
        typeof(ICampServiceRead).IsAssignableFrom(typeof(CachingCampService))
            .Should().BeTrue();
    }

    [HumansFact]
    public void CachingCampService_ImplementsICampRoleCampAccess()
    {
        typeof(ICampRoleCampAccess).IsAssignableFrom(typeof(CachingCampService))
            .Should().BeTrue(
                because: "CampRoleService must use the decorator-backed port so migration writes still invalidate CampInfo");
    }

    [HumansFact]
    public void ICampService_And_ICampServiceRead_ResolveToSameSingleton()
    {
        // Mirrors the Camps-section DI shape: the same CachingCampService
        // singleton is exposed under both interface keys.
        var services = new ServiceCollection();
        services.AddSingleton(Substitute.For<ICampRepository>());
        services.AddSingleton(Substitute.For<IServiceScopeFactory>());
        services.AddSingleton(Substitute.For<IClock>());
        services.AddSingleton(Substitute.For<ILogger<CachingCampService>>());

        services.AddSingleton<CachingCampService>();
        services.AddSingleton<ICampService>(sp => sp.GetRequiredService<CachingCampService>());
        services.AddSingleton<ICampServiceRead>(sp => sp.GetRequiredService<CachingCampService>());
        services.AddSingleton<ICampRoleCampAccess>(sp => sp.GetRequiredService<CachingCampService>());

        using var provider = services.BuildServiceProvider();

        var fromFull = provider.GetRequiredService<ICampService>();
        var fromRead = provider.GetRequiredService<ICampServiceRead>();
        var fromRoleAccess = provider.GetRequiredService<ICampRoleCampAccess>();
        var concrete = provider.GetRequiredService<CachingCampService>();

        ReferenceEquals(fromFull, concrete).Should().BeTrue();
        ReferenceEquals(fromRead, concrete).Should().BeTrue();
        ReferenceEquals(fromRoleAccess, concrete).Should().BeTrue();
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

    // ── Public detail page — EE non-exposure invariant ───────────────────────

    /// <summary>
    /// Pins the invariant: the public camp detail page can never render Early Entry
    /// state because the view-model shape rendered by the public detail page contains
    /// no EE-related properties.
    /// Guards against future accidental additions (e.g., HasEarlyEntry, EeSlotCount,
    /// EeStartDate, IsEarlyAccess) by matching on name substrings / prefixes.
    /// Issue #490: EE state is admin-only and must never appear on anonymous views.
    /// </summary>
    [HumansFact]
    public void PublicCampDetail_DoesNotExposeEarlyEntryState()
    {
        // All view-model types that compose the public detail page shape.
        var publicDetailTypes = new[]
        {
            typeof(CampDetailViewModel),
            typeof(CampSeasonDetailViewModel),
        };

        var eeProperties = publicDetailTypes
            .SelectMany(t => t.GetProperties())
            .Where(p => p.Name.Contains("EarlyEntry", StringComparison.OrdinalIgnoreCase)
                        || p.Name.StartsWith("Ee", StringComparison.Ordinal))
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}")
            .ToList();

        eeProperties.Should().BeEmpty(
            because: "Early Entry state (HasEarlyEntry, EeSlotCount, EeStartDate, etc.) must never be " +
                     "projected into the public detail view shape - it is admin-only (issue #490, spec §4.4)");
    }
}
