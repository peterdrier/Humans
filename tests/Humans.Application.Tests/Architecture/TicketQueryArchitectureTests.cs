using AwesomeAssertions;
using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Campaigns;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
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
/// decorator (Infrastructure) owns the per-order <c>TicketOrderInfo</c>
/// projection and the per-user short-TTL entries; it is the only impl that
/// touches <c>IMemoryCache</c> in this section.
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
            because: "the inner TicketQueryService is cache-free per T-07; CachingTicketQueryService owns the projection and the per-user short-TTL entries");
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
        paramTypes.Should().Contain(typeof(ITeamService));
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
            because: "the caching decorator is terminal — section-internal logic stays on the inner service and the projection layout is private to the decorator");
    }

    [HumansFact]
    public void CachingTicketQueryService_ImplementsITicketQueryService()
    {
        typeof(ITicketQueryService).IsAssignableFrom(typeof(CachingTicketQueryService))
            .Should().BeTrue();
    }

    [HumansFact]
    public void CachingTicketQueryService_ImplementsITicketCacheInvalidator()
    {
        typeof(ITicketCacheInvalidator).IsAssignableFrom(typeof(CachingTicketQueryService))
            .Should().BeTrue(
                because: "the decorator owns the cache layout, so it owns the invalidation seam that external write-side callers (sync, merge fold) poke");
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
