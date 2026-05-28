using AwesomeAssertions;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Enums;
using Humans.Infrastructure.Repositories.Camps;
using Humans.Infrastructure.Services.Camps;
using Humans.Web.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NodaTime;
using NSubstitute;
using CampRoleService = Humans.Application.Services.Camps.CampRoleService;
using CampService = Humans.Application.Services.Camps.CampService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests for the Camps section — §15 caching decorator (T-06, 2026-05-16). Pins:
/// <list type="bullet">
/// <item><c>CachingCampService</c> wraps <c>CampService</c> with a Singleton, hit-tracked
///   per-camp projection plus a separate CampSettingsInfo slot.</item>
/// <item>The set of types that depend on <see cref="ICampRepository"/> is
///   pinned — no one outside the approved list can bypass the decorator and
///   write to the Camps tables behind its back. This is what replaces the
///   T-06 SaveChanges-interceptor backstop.</item>
/// </list>
/// </summary>
public class CampsArchitectureTests
{
    // ── CampService (inner) ──────────────────────────────────────────────────

    [HumansFact]
    public void CampService_ConstructorTakesNoStoreType()
    {
        var ctor = typeof(CampService).GetConstructors().Single();
        var storeParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Humans.Application.Interfaces.Stores", StringComparison.Ordinal));

        storeParam.Should().BeNull(
            because: "Camps §15 follows the no-store pattern; decorator owns the dict directly per §15d");
    }

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
    public void CachingCampService_IsSealed()
    {
        typeof(CachingCampService).IsSealed.Should().BeTrue(
            because: "decorator implementations are sealed to prevent override of cache-invalidation semantics");
    }

    [HumansFact]
    public void CachingCampService_LivesInInfrastructureServicesCampsNamespace()
    {
        typeof(CachingCampService).Namespace
            .Should().Be("Humans.Infrastructure.Services.Camps",
                because: "§15d decorators live in Humans.Infrastructure.Services.<Section>");
    }

    // ── No-bypass tripwire (replaces the T-06 SaveChanges interceptor) ───────

    /// <summary>
    /// Pins the set of types that may inject <see cref="ICampRepository"/>.
    /// The decorator's invalidation contract holds only as long as every
    /// write to the Camps tables goes through an <see cref="ICampService"/>
    /// method the decorator wraps. New consumers that take the repo
    /// directly — especially write-shaped consumers — can skip the
    /// invalidate-after-mutate call and reintroduce the
    /// <see cref="CampSeasonInfo.EeGrantedCount"/> drift that T-06 fixed.
    /// If you're adding a legitimate new caller, update this list and the
    /// remarks on <c>CachingCampService</c> together.
    /// </summary>
    [HumansFact]
    public void ICampRepository_HasNoUnexpectedConsumers()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        {
            // Reads + writes — the inner service is the only writer.
            "Humans.Application.Services.Camps.CampService",
            // Repo implementation itself.
            "Humans.Infrastructure.Repositories.Camps.CampRepository",
            // Decorator — reads for the per-camp refresh / settings reload.
            "Humans.Infrastructure.Services.Camps.CachingCampService",
            // Read-only consumer — uses GetActiveLeadUserIdsAsync /
            // IsLeadAnywhereAsync. Never writes through the repo.
            "Humans.Infrastructure.Jobs.SystemTeamSyncJob",
            // CampLead retirement (issue nobodies-collective/Humans#753) —
            // SeedSystemRolesAndMigrateLeadsAsync reads from the legacy
            // camp_leads + camp_seasons + camp_members tables to migrate
            // existing leads into CampRoleAssignment. Routed through
            // ICampRoleCampAccess.EnsureActiveMemberForMigrationAsync via the
            // decorator (which invalidates), so writes still pass through the
            // decorator path. Read-only consumer here.
            "Humans.Application.Services.Camps.CampRoleService",
        };

        var assemblies = new[]
        {
            typeof(CampService).Assembly,
            typeof(CampRepository).Assembly,
            typeof(CachingCampService).Assembly,
        };

        var consumers = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => t.GetConstructors()
                .Any(c => c.GetParameters().Any(p => p.ParameterType == typeof(ICampRepository))))
            .Select(t => t.FullName ?? t.Name)
            .ToList();

        var unexpected = consumers.Where(c => !allowed.Contains(c)).ToList();

        unexpected.Should().BeEmpty(
            because: "T-06: every write to the Camps tables must go through ICampService so the decorator can invalidate the affected camp. If this new type only reads, add it to the allow-list with a comment explaining why; if it writes, route through ICampService instead.");
    }

    /// <summary>
    /// Belt-on-belt: ensure the obsolete <c>CampInfoSaveChangesInterceptor</c>
    /// hasn't crept back into the Infrastructure assembly. The decorator's
    /// inline invalidation is the only path; a re-added interceptor would
    /// double-fire invalidations and re-introduce the per-row
    /// <c>CampSeason</c>→<c>CampId</c> round trip the no-bypass rule was
    /// designed to retire.
    /// </summary>
    [HumansFact]
    public void CampInfoSaveChangesInterceptor_IsNotPresent()
    {
        var found = typeof(CachingCampService).Assembly
            .GetTypes()
            .FirstOrDefault(t => string.Equals(t.Name, "CampInfoSaveChangesInterceptor", StringComparison.Ordinal));

        found.Should().BeNull(
            because: "the T-06 SaveChanges interceptor was retired in favour of decorator-only invalidation pinned by ICampRepository_HasNoUnexpectedConsumers — see CachingCampService remarks");
    }

    // ── CampLead ─────────────────────────────────────────────────────────────

    [HumansFact]
    public void CampLead_HasNoUserNavigationProperty()
    {
        typeof(Humans.Domain.Entities.CampLead)
            .GetProperty("User")
            .Should().BeNull(
                because: "CampLead.User is a cross-domain nav into the Users section; resolve via IUserService instead (design-rules §6c)");
    }

    [HumansFact]
    public void CampLead_KeepsUserIdForeignKey()
    {
        typeof(Humans.Domain.Entities.CampLead)
            .GetProperty("UserId")
            .Should().NotBeNull(
                because: "FK stays — only the navigation property is stripped");
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
                            "Humans.Application.Services.Camps", StringComparison.Ordinal))
            .Select(t => t.FullName ?? t.Name)
            .ToList();

        campsClaimants.Should().BeEquivalentTo([typeof(CampRoleService).FullName!],
            because: "CampRoleService is the only Camps-side IGoogleGroupMembershipSource claimant; new Camps groups must route through this service so the orchestrator's collision check sees one Camps voice per group key (issue nobodies-collective/Humans#740)");
    }

    /// <summary>
    /// Issue nobodies-collective/Humans#740: the Camps section exposes its
    /// Google Group claims through <see cref="IGoogleGroupMembershipSource"/>
    /// only — the orchestrator (<c>GoogleGroupSyncService</c>) pulls; sections
    /// never push. The "post-commit RequestSyncAsync nudge" pattern was
    /// removed, and provisioning of missing groups moved into the orchestrator.
    /// Pins that CampRoleService takes neither <c>IGoogleGroupSync</c> nor
    /// <c>IGoogleGroupProvisioningClient</c> in its constructor.
    /// </summary>
    [HumansFact]
    public void CampRoleService_DoesNotDependOnGoogleSyncOrProvisioning()
    {
        var ctor = typeof(CampRoleService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().NotContain(typeof(IGoogleGroupSync),
            because: "sections must not call IGoogleGroupSync.RequestSyncAsync; the orchestrator pulls from IGoogleGroupMembershipSource (issue nobodies-collective/Humans#740)");
        paramTypes.Should().NotContain(typeof(IGoogleGroupProvisioningClient),
            because: "group provisioning moved into GoogleGroupSyncService.ReconcileClaimAsync; sections do not provision groups (issue nobodies-collective/Humans#740)");
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
