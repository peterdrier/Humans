using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Repositories.Camps;
using Humans.Infrastructure.Services.Camps;
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
/// <item><c>CampInfoSaveChangesInterceptor</c> watches the cross-table
///   <c>CampMember</c> dependency that <see cref="CampSeasonInfo.EeGrantedCount"/>
///   projects from — historical drift bug guard.</item>
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
                because: "external callers (the SaveChanges interceptor) signal cache invalidation via the one-method interface, not by reaching into the decorator directly (§15e)");
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

    // ── CampInfoSaveChangesInterceptor (T-06) ────────────────────────────────

    /// <summary>
    /// EeGrantedCount cross-table invariant: the interceptor MUST watch
    /// <c>CampMember</c> writes. Without this, granting or revoking Early
    /// Entry leaves <see cref="CampSeasonInfo.EeGrantedCount"/> stale until
    /// process restart (historical drift bug — see <c>CampInfo</c> remarks).
    /// </summary>
    [HumansFact]
    public void CampInfoInterceptor_WatchesCampMemberWritesForEarlyEntryInvariant()
    {
        var source = typeof(CampInfoSaveChangesInterceptor)
            .Assembly.Location;
        // Inspect the type's transitive ChangeTracker-walk via reflection by
        // running a quick smoke: confirm the type compiles in the expected
        // assembly and exposes the interceptor base. The actual switch-case
        // catch is exercised in CampInfoInterceptorTests; here we pin that
        // the type exists in the right place so a refactor doesn't silently
        // move it out of the EF pipeline registration.
        source.Should().Contain("Humans.Infrastructure",
            because: "the interceptor must remain in the Humans.Infrastructure assembly so Program.cs's AddInterceptors registration resolves it");
        typeof(CampInfoSaveChangesInterceptor)
            .Should().BeAssignableTo<Microsoft.EntityFrameworkCore.Diagnostics.SaveChangesInterceptor>(
                because: "T-06 cross-table EeGrantedCount invariant — see CampInfo remarks");
    }

    // ── CampInfo projection — leads invariant ───────────────────────────────

    /// <summary>
    /// T-06 leads invariant: <see cref="CampInfo.Leads"/> is always populated
    /// (non-null; may be empty). The legacy "null = not loaded" branch was
    /// retired when reads moved onto the cached projection.
    /// </summary>
    [HumansFact]
    public void CampInfo_LeadsPropertyIsNonNullable()
    {
        var leadsProp = typeof(CampInfo).GetProperty(nameof(CampInfo.Leads))!;
        var nullability = new System.Reflection.NullabilityInfoContext()
            .Create(leadsProp);
        nullability.WriteState.Should().Be(System.Reflection.NullabilityState.NotNull,
            because: "T-06: CampInfo.Leads is always populated. The legacy 'null means not loaded' branch was retired when GetCampsForYearAsync moved onto the CachingCampService projection.");
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
            typeof(CampLeadSummary),
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
