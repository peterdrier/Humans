using AwesomeAssertions;
using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Campaigns;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Tickets;
using Humans.Infrastructure.Repositories.Tickets;
using Humans.Infrastructure.Services.Tickets;
using TicketQueryService = Humans.Application.Services.Tickets.TicketQueryService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 repository pattern for the Tickets
/// query surface — migrated per issue nobodies-collective/Humans#545 sub-task
/// #545a, decorator lifted per T-07.
///
/// <para>
/// The inner <see cref="TicketQueryService"/> lives in Application, goes
/// through <see cref="ITicketRepository"/>, and never imports EF types or
/// <c>IMemoryCache</c>. The Singleton <see cref="CachingTicketQueryService"/>
/// decorator (Infrastructure) owns the tracked ticket slices and invalidation
/// seam; it is the only impl that touches <c>IMemoryCache</c> in this section
/// for the per-event vendor summary.
/// </para>
/// </summary>
public class TicketQueryArchitectureTests
{
    // ── TicketQueryService (inner) ───────────────────────────────────────────

    [HumansFact]
    public void TicketQueryService_IsSealed()
    {
        typeof(TicketQueryService).IsSealed.Should().BeTrue(
            because: "Application-layer services are sealed to prevent ad-hoc subclassing; new behavior belongs on the interface");
    }

    [HumansFact]
    public void TicketQueryService_TakesRepository()
    {
        var ctor = typeof(TicketQueryService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(ITicketRepository),
            because: "all ticket-table access must flow through ITicketRepository");
    }

    [HumansFact]
    public void TicketQueryService_HasNoIMemoryCacheConstructorParameter()
    {
        var ctor = typeof(TicketQueryService).GetConstructors().Single();
        var cachingParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Microsoft.Extensions.Caching.Memory", StringComparison.Ordinal));

        cachingParam.Should().BeNull(
            because: "the inner TicketQueryService is cache-free per T-07; CachingTicketQueryService owns only short-TTL cache entries");
    }

    [HumansFact]
    public void TicketQueryService_RoutesCrossSectionReadsThroughServices()
    {
        var ctor = typeof(TicketQueryService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        // Cross-section reads go through the owning services, not other repositories.
        paramTypes.Should().Contain(typeof(IBudgetService));
        paramTypes.Should().Contain(typeof(ICampaignService));
        paramTypes.Should().Contain(typeof(IUserService));
        paramTypes.Should().Contain(typeof(IUserEmailService));
        paramTypes.Should().Contain(typeof(ITeamServiceRead));
        paramTypes.Should().Contain(typeof(IShiftManagementService));
    }

    [HumansFact]
    public void TicketQueryService_TakesNoOtherSectionRepository()
    {
        var ctor = typeof(TicketQueryService).GetConstructors().Single();
        var otherRepos = ctor.GetParameters()
            .Where(p => typeof(IUserRepository).IsAssignableFrom(p.ParameterType) ||
                        typeof(IProfileRepository).IsAssignableFrom(p.ParameterType) ||
                        typeof(IUserEmailRepository).IsAssignableFrom(p.ParameterType) ||
                        typeof(IApplicationRepository).IsAssignableFrom(p.ParameterType))
            .ToList();

        otherRepos.Should().BeEmpty(
            because: "cross-section reads go through the owning service, not another section's repository (design-rules §2c)");
    }

    // ── CachingTicketQueryService (decorator) ────────────────────────────────

    [HumansFact]
    public void CachingTicketQueryService_IsSealed()
    {
        typeof(CachingTicketQueryService).IsSealed.Should().BeTrue(
            because: "the caching decorator is terminal — section-internal logic stays on the inner service and cache layout is private to the decorator");
    }

    [HumansFact]
    public void CachingTicketQueryService_ImplementsITicketService()
    {
        typeof(ITicketService).IsAssignableFrom(typeof(CachingTicketQueryService))
            .Should().BeTrue();
    }

    [HumansFact]
    public void ITicketService_InheritsITicketServiceRead()
    {
        typeof(ITicketServiceRead).IsAssignableFrom(typeof(ITicketService))
            .Should().BeTrue(
                because: "ITicketService is the full Tickets surface; external sections inject the narrow ITicketServiceRead");
    }

    [HumansFact]
    public void CachingTicketQueryService_ImplementsITicketServiceRead()
    {
        typeof(ITicketServiceRead).IsAssignableFrom(typeof(CachingTicketQueryService))
            .Should().BeTrue();
    }

    [HumansFact]
    public void ITicketServiceRead_ExposesNoEntityTypes()
    {
        var offenders = typeof(ITicketServiceRead).GetMethods()
            .SelectMany(m => m.ReturnParameter.ParameterType
                .GetGenericArguments()
                .Append(m.ReturnParameter.ParameterType)
                .Concat(m.GetParameters().Select(p => p.ParameterType))
                .SelectMany(FlattenType))
            .Where(t => string.Equals(t.Namespace, "Humans.Domain.Entities", StringComparison.Ordinal))
            .Select(t => t.Name)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            because: "ITicketServiceRead is the cross-section read contract and must not expose EF entity types");

        static IEnumerable<Type> FlattenType(Type type)
        {
            yield return type;
            foreach (var arg in type.GetGenericArguments())
                foreach (var nested in FlattenType(arg))
                    yield return nested;
        }
    }

    [HumansFact]
    public void CachingTicketQueryService_ImplementsITicketCacheInvalidator()
    {
        typeof(ITicketCacheInvalidator).IsAssignableFrom(typeof(CachingTicketQueryService))
            .Should().BeTrue(
                because: "the decorator owns the cache layout, so it owns the invalidation seam that external write-side callers (sync, merge fold) poke");
    }

    [HumansFact]
    public void CachingTicketQueryService_IsTheOnlyImplementationOfITicketCacheInvalidator()
    {
        // Only the Singleton decorator may implement the invalidator seam. If
        // a second impl ever appears in src/, the decorator stops being the
        // single cache-truth — write-side callers can no longer rely on a
        // poke reaching every key. Catches accidental "helper" classes that
        // claim the interface and silently do partial eviction.
        var assemblies = new[]
        {
            typeof(TicketQueryService).Assembly,            // Humans.Application
            typeof(CachingTicketQueryService).Assembly,     // Humans.Infrastructure
        };

        var impls = assemblies
            .SelectMany(a => a.GetTypes())
            .Where(t => !t.IsInterface
                        && !t.IsAbstract
                        && typeof(ITicketCacheInvalidator).IsAssignableFrom(t))
            .ToList();

        impls.Should().ContainSingle(
            because: "ITicketCacheInvalidator is the one-way seam owned by the Singleton decorator; a second impl would split cache truth")
            .Which.Should().Be(typeof(CachingTicketQueryService));
    }

    [HumansFact]
    public void TicketQueryService_DoesNotImplementITicketCacheInvalidator()
    {
        typeof(ITicketCacheInvalidator).IsAssignableFrom(typeof(TicketQueryService))
            .Should().BeFalse(
                because: "cache eviction belongs on the Singleton decorator seam, not on the scoped inner service or the ticket read/query contract");
    }

    [HumansFact]
    public void ITicketServiceRead_DoesNotExposeHasCurrentEventTicketAsync()
    {
        typeof(ITicketServiceRead).GetMethod("HasCurrentEventTicketAsync")
            .Should().BeNull(
                because: "current-event ticket status is carried by UserTicketHoldings instead of a separate read method");
    }

    [HumansFact]
    public void TicketSyncService_TakesITicketCacheInvalidator()
    {
        // The Tickets-section write site (sync job + IUserMerge fold) must
        // hold a reference to the invalidator so every commit poke reaches
        // the decorator's projection. Mirrors the Shifts fan-in invariant
        // pinned in ShiftViewArchitectureTests.
        var ctor = typeof(TicketSyncService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(ITicketCacheInvalidator),
            because: "TicketSyncService drives InvalidateAll (post-sync) and InvalidateAfterUserMerge (ReassignAsync) — it must take the seam through DI so the decorator owns cache eviction");
    }

    // ── ITicketRepository ────────────────────────────────────────────────────

    [HumansFact]
    public void TicketRepository_IsSealed()
    {
        var repoType = typeof(TicketRepository);

        repoType.IsSealed.Should().BeTrue(
            because: "repository implementations are sealed to prevent ad-hoc extension; any new behavior belongs on the interface");
    }
}
