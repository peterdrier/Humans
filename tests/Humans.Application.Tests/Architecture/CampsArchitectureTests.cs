using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Infrastructure.Repositories.Camps;
using Humans.Infrastructure.Services.Camps;
using CampRoleService = Humans.Application.Services.Camps.CampRoleService;
using CampService = Humans.Application.Services.Camps.CampService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests for the Camps section — §15 repository pattern
/// (issue #542, 2026-04-22) and T-06 caching decorator (2026-05-16). Pins:
/// <list type="bullet">
/// <item><c>CampService</c> goes through <see cref="ICampRepository"/>,
///   never injects DbContext or IMemoryCache.</item>
/// <item><c>CachingCampService</c> wraps it with a Singleton, hit-tracked
///   per-camp projection plus a separate CampSettingsInfo slot.</item>
/// <item>The set of types that depend on <see cref="ICampRepository"/> is
///   pinned — no one outside the approved list can bypass the decorator and
///   write to the Camps tables behind its back. This is what replaces the
///   T-06 SaveChanges-interceptor backstop.</item>
/// <item><see cref="CampInfo.Leads"/> is non-nullable.</item>
/// </list>
/// </summary>
public class CampsArchitectureTests
{
    // ── CampService (inner) ──────────────────────────────────────────────────

    [HumansFact]
    public void CampService_TakesRepository()
    {
        var ctor = typeof(CampService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(ICampRepository));
    }

    [HumansFact]
    public void CampService_TakesUserService()
    {
        var ctor = typeof(CampService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IUserService),
            because: "lead display names are resolved via IUserService cross-section (design-rules §6, §9); CampLead.User nav is stripped");
    }

    [HumansFact]
    public void CampService_TakesImageStorage()
    {
        var ctor = typeof(CampService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IFileStorage),
            because: "filesystem I/O is delegated to the shared IFileStorage abstraction — the Application project can't touch System.IO directly (design-rules §1)");
    }

    /// <summary>
    /// T-06 pin: the inner CampService is cache-unaware. The canonical
    /// caching layer lives on the Singleton <c>CachingCampService</c> decorator
    /// per §15c. Reaching back into IMemoryCache here would resurrect the
    /// pre-T-06 5-minute-TTL shortcut that this PR retired.
    /// </summary>
    [HumansFact]
    public void CampService_HasNoIMemoryCacheConstructorParameter()
    {
        var ctor = typeof(CampService).GetConstructors().Single();
        var cachingParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Microsoft.Extensions.Caching.Memory", StringComparison.Ordinal));

        cachingParam.Should().BeNull(
            because: "T-06: inner CampService is cache-unaware; CachingCampService owns the §15 projection");
    }

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

    // ── ICampRepository ──────────────────────────────────────────────────────

    [HumansFact]
    public void CampRepository_IsSealed()
    {
        var repoType = typeof(CampRepository);

        repoType.IsSealed.Should().BeTrue(
            because: "repository implementations are sealed to prevent ad-hoc extension; any new behavior belongs on the interface");
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
            // ICampService.AddCampMemberToActiveSeasonAsLeadAsync via the
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

    // ── Public detail page — EE non-exposure invariant ───────────────────────

    /// <summary>
    /// Pins the invariant: the public camp detail page can never render Early Entry
    /// state because the data shape returned by BuildCampDetailDataAsync — and every
    /// record type reachable from it — contains no EE-related properties.
    /// Guards against future accidental additions (e.g., HasEarlyEntry, EeSlotCount,
    /// EeStartDate, IsEarlyAccess) by matching on name substrings / prefixes.
    /// Issue #490: EE state is admin-only and must never appear on anonymous views.
    /// </summary>
    [HumansFact]
    public void PublicCampDetail_DoesNotExposeEarlyEntryState()
    {
        // All record types that compose the public detail data shape.
        var publicDetailTypes = new[]
        {
            typeof(CampDetailData),
            typeof(CampSeasonDetailData),
        };

        var eeProperties = publicDetailTypes
            .SelectMany(t => t.GetProperties())
            .Where(p => p.Name.Contains("EarlyEntry", StringComparison.OrdinalIgnoreCase)
                        || p.Name.StartsWith("Ee", StringComparison.Ordinal))
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}")
            .ToList();

        eeProperties.Should().BeEmpty(
            because: "Early Entry state (HasEarlyEntry, EeSlotCount, EeStartDate, etc.) must never be " +
                     "projected into the public detail data shape — it is admin-only (issue #490, spec §4.4)");
    }
}
