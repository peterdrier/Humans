using AwesomeAssertions;
using Humans.Application.Interfaces.Campaigns;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Tickets;
using Humans.Application.Interfaces.Users;
using Microsoft.EntityFrameworkCore;
using Xunit;
using TicketSyncService = Humans.Application.Services.Tickets.TicketSyncService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 repository pattern for
/// <see cref="TicketSyncService"/> — domain-persistence side migrated in
/// PR #545c (umbrella #545).
///
/// <para>
/// The Ticket Tailor API / webhook side remains in <c>Humans.Infrastructure</c>
/// as a vendor connector (<c>ITicketVendorService</c> →
/// <c>TicketTailorService</c> / <c>StubTicketVendorService</c>). These tests
/// pin the domain-persistence side's shape: Application-layer service, no
/// DbContext, all DB access via <c>ITicketRepository</c>, and cross-section
/// reads/writes routed through the owning services (not a second repo).
/// </para>
/// </summary>
public class TicketSyncArchitectureTests
{
    // ── TicketSyncService ────────────────────────────────────────────────────

    [Fact]
    public void TicketSyncService_LivesInHumansApplicationServicesTicketsNamespace()
    {
        typeof(TicketSyncService).Namespace
            .Should().Be("Humans.Application.Services.Tickets",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [Fact]
    public void TicketSyncService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(TicketSyncService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — use ITicketRepository instead (design-rules §3)");
    }

    [Fact]
    public void TicketSyncService_TakesRepository()
    {
        var ctor = typeof(TicketSyncService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(ITicketRepository),
            because: "TicketSyncService's DB access must go through ITicketRepository (design-rules §3)");
    }

    [Fact]
    public void TicketSyncService_TakesVendorConnectorInterface()
    {
        var ctor = typeof(TicketSyncService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(ITicketVendorService),
            because: "Ticket Tailor API calls are the connector's job — the sync service delegates all vendor I/O to ITicketVendorService (Infrastructure-backed)");
    }

    [Fact]
    public void TicketSyncService_TakesUserServiceForEventParticipationWrites()
    {
        var ctor = typeof(TicketSyncService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IUserService),
            because: "event_participations is User-section-owned per PR #243; TicketSync must route participation writes through IUserService (design-rules §8, §9)");
    }

    [Fact]
    public void TicketSyncService_TakesCampaignServiceForGrantRedemption()
    {
        var ctor = typeof(TicketSyncService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(ICampaignService),
            because: "campaign_grants is Campaigns-section-owned; redemption writes route through ICampaignService (design-rules §8, §9)");
    }

    [Fact]
    public void TicketSyncService_TakesShiftManagementServiceForActiveEventLookup()
    {
        var ctor = typeof(TicketSyncService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IShiftManagementService),
            because: "event_settings is Shifts-section-owned; active-event reads route through IShiftManagementService rather than a direct DbContext read (design-rules §8, §9)");
    }

    [Fact]
    public void TicketSyncService_ConstructorTakesNoStoreType()
    {
        var ctor = typeof(TicketSyncService).GetConstructors().Single();
        var storeParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Humans.Application.Interfaces.Stores", StringComparison.Ordinal));

        storeParam.Should().BeNull(
            because: "Application services must not depend on store abstractions (design-rules §15); the Tickets section's §15 migration does not use a store");
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
        // Mirrors ProfileRepository/UserRepository — repository implementations
        // are terminal; no subclass should extend or override the EF-backed
        // data access.
        var repoType = typeof(Humans.Infrastructure.Repositories.TicketRepository);

        repoType.IsSealed.Should().BeTrue(
            because: "repository implementations are sealed to prevent ad-hoc extension; any new behavior belongs on the interface");
    }

    [Fact]
    public void TicketRepository_ImplementsITicketRepository()
    {
        typeof(ITicketRepository).IsAssignableFrom(typeof(Humans.Infrastructure.Repositories.TicketRepository))
            .Should().BeTrue();
    }
}
