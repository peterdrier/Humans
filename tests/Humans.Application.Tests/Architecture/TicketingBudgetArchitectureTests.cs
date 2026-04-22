using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Microsoft.EntityFrameworkCore;
using Xunit;
using TicketingBudgetService = Humans.Application.Services.Tickets.TicketingBudgetService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 pattern for the Tickets→Budget bridge
/// service — migrated in PR #545b (umbrella #545).
///
/// <para>
/// <c>TicketingBudgetService</c> is a narrow bridge: reads paid ticket orders
/// through <see cref="ITicketingBudgetRepository"/> and delegates all
/// Budget-owned mutations (line items and projections) to
/// <see cref="IBudgetService"/>. No caching decorator — it is called from a
/// nightly batch job and a few admin-finance refresh buttons, nowhere close to
/// justifying a decorator.
/// </para>
/// </summary>
public class TicketingBudgetArchitectureTests
{
    // ── TicketingBudgetService ───────────────────────────────────────────────

    [Fact]
    public void TicketingBudgetService_LivesInHumansApplicationServicesTicketsNamespace()
    {
        typeof(TicketingBudgetService).Namespace
            .Should().Be("Humans.Application.Services.Tickets",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [Fact]
    public void TicketingBudgetService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(TicketingBudgetService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — use ITicketingBudgetRepository instead (design-rules §3)");
    }

    [Fact]
    public void TicketingBudgetService_HasNoIMemoryCacheConstructorParameter()
    {
        var ctor = typeof(TicketingBudgetService).GetConstructors().Single();
        var cachingParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Microsoft.Extensions.Caching.Memory", StringComparison.Ordinal));

        cachingParam.Should().BeNull(
            because: "TicketingBudgetService is a batch-style bridge; no per-request caching is warranted (design-rules §15 + Governance/User precedent for decorator-less sections)");
    }

    [Fact]
    public void TicketingBudgetService_TakesRepository()
    {
        var ctor = typeof(TicketingBudgetService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(ITicketingBudgetRepository));
    }

    [Fact]
    public void TicketingBudgetService_TakesBudgetServiceForCrossSectionWrites()
    {
        var ctor = typeof(TicketingBudgetService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IBudgetService),
            because: "ticketing_projections and budget_line_items are Budget-owned; all mutations route through IBudgetService (design-rules §2c, §8)");
    }

    [Fact]
    public void TicketingBudgetService_ConstructorTakesNoStoreType()
    {
        var ctor = typeof(TicketingBudgetService).GetConstructors().Single();
        var storeParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Humans.Application.Interfaces.Stores", StringComparison.Ordinal));

        storeParam.Should().BeNull(
            because: "the store pattern is retired (design-rules §15); §15 sections use in-decorator ConcurrentDictionary when caching is warranted, or no cache at all");
    }

    // ── ITicketingBudgetRepository ───────────────────────────────────────────

    [Fact]
    public void ITicketingBudgetRepository_LivesInApplicationInterfacesRepositoriesNamespace()
    {
        typeof(ITicketingBudgetRepository).Namespace
            .Should().Be("Humans.Application.Interfaces.Repositories",
                because: "repository interfaces live in Humans.Application.Interfaces.Repositories per design-rules §3");
    }

    [Fact]
    public void TicketingBudgetRepository_IsSealed()
    {
        // Mirrors ProfileRepository / UserRepository — repository implementations are terminal;
        // no subclass should extend or override the EF-backed data access.
        var repoType = typeof(Humans.Infrastructure.Repositories.TicketingBudgetRepository);

        repoType.IsSealed.Should().BeTrue(
            because: "repository implementations are sealed to prevent ad-hoc extension; any new behavior belongs on the interface");
    }
}
