using AwesomeAssertions;
using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Campaigns;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Microsoft.EntityFrameworkCore;
using Xunit;
using TicketQueryService = Humans.Application.Services.Tickets.TicketQueryService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 repository pattern for the Tickets
/// query surface — migrated per issue nobodies-collective/Humans#545 sub-task
/// #545a.
///
/// <para>
/// The Tickets query service is migrated without a caching decorator. Reads
/// back two admin dashboards and one guest-facing "you have tickets" probe;
/// neither is hot-path enough at our ~500-user scale to justify a decorator
/// (same rationale User/Governance used). These tests enforce the non-decorator
/// shape: <see cref="TicketQueryService"/> lives in Application, goes through
/// <see cref="ITicketRepository"/>, and never imports EF types directly.
/// </para>
/// </summary>
public class TicketQueryArchitectureTests
{
    // ── TicketQueryService ───────────────────────────────────────────────────

    [Fact]
    public void TicketQueryService_LivesInHumansApplicationServicesTicketsNamespace()
    {
        typeof(TicketQueryService).Namespace
            .Should().Be("Humans.Application.Services.Tickets",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [Fact]
    public void TicketQueryService_IsSealed()
    {
        typeof(TicketQueryService).IsSealed.Should().BeTrue(
            because: "Application-layer services are sealed to prevent ad-hoc subclassing; new behavior belongs on the interface");
    }

    [Fact]
    public void TicketQueryService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(TicketQueryService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — use ITicketRepository (design-rules §3)");
    }

    [Fact]
    public void TicketQueryService_TakesRepository()
    {
        var ctor = typeof(TicketQueryService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(ITicketRepository),
            because: "all ticket-table access must flow through ITicketRepository");
    }

    [Fact]
    public void TicketQueryService_RoutesCrossSectionReadsThroughServices()
    {
        var ctor = typeof(TicketQueryService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        // Cross-section reads go through the owning services, not other repositories.
        paramTypes.Should().Contain(typeof(IBudgetService));
        paramTypes.Should().Contain(typeof(ICampaignService));
        paramTypes.Should().Contain(typeof(IUserService));
        paramTypes.Should().Contain(typeof(IUserEmailService));
        paramTypes.Should().Contain(typeof(IProfileService));
        paramTypes.Should().Contain(typeof(ITeamService));
        paramTypes.Should().Contain(typeof(IShiftManagementService));
    }

    [Fact]
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

    // ── ITicketRepository ────────────────────────────────────────────────────

    [Fact]
    public void ITicketRepository_LivesInApplicationInterfacesRepositoriesNamespace()
    {
        typeof(ITicketRepository).Namespace
            .Should().Be("Humans.Application.Interfaces.Repositories",
                because: "repository interfaces live in Humans.Application.Interfaces.Repositories per design-rules §3");
    }

    [Fact]
    public void TicketRepository_IsSealed()
    {
        var repoType = typeof(Humans.Infrastructure.Repositories.TicketRepository);

        repoType.IsSealed.Should().BeTrue(
            because: "repository implementations are sealed to prevent ad-hoc extension; any new behavior belongs on the interface");
    }
}
