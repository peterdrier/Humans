using AwesomeAssertions;
using Humans.Application.Interfaces.Budget;
using Humans.Application.Interfaces.Finance;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Repositories.Finance;
using Microsoft.EntityFrameworkCore;
using Xunit;
using HoldedSyncService = Humans.Application.Services.Finance.HoldedSyncService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests pinning the §15 Clean Architecture shape for the
/// Finance section's Holded read-side integration.
///
/// <para>
/// <see cref="HoldedSyncService"/> orchestrates the read-only sync of Holded
/// purchase docs and resolves them against Budget categories via tag slugs.
/// These tests pin: namespace placement, no <see cref="DbContext"/> dependency,
/// DB access via <see cref="IHoldedRepository"/>, cross-section reads through
/// <see cref="IBudgetService"/> (never <see cref="IBudgetRepository"/>),
/// vendor I/O through <see cref="IHoldedClient"/>, and a sealed repository
/// implementation.
/// </para>
/// </summary>
public class FinanceArchitectureTests
{
    [HumansFact]
    public void HoldedSyncService_LivesInHumansApplicationServicesFinanceNamespace()
    {
        typeof(HoldedSyncService).Namespace
            .Should().Be("Humans.Application.Services.Finance");
    }

    [HumansFact]
    public void HoldedSyncService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(HoldedSyncService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(p => typeof(DbContext).IsAssignableFrom(p.ParameterType));
    }

    [HumansFact]
    public void HoldedSyncService_TakesHoldedRepository()
    {
        var ctor = typeof(HoldedSyncService).GetConstructors().Single();
        ctor.GetParameters().Select(p => p.ParameterType).Should().Contain(typeof(IHoldedRepository));
    }

    [HumansFact]
    public void HoldedSyncService_TakesBudgetServiceForCrossSectionReads()
    {
        var ctor = typeof(HoldedSyncService).GetConstructors().Single();
        ctor.GetParameters().Select(p => p.ParameterType).Should().Contain(typeof(IBudgetService));
    }

    [HumansFact]
    public void HoldedSyncService_DoesNotTakeBudgetRepository()
    {
        var ctor = typeof(HoldedSyncService).GetConstructors().Single();
        ctor.GetParameters().Select(p => p.ParameterType)
            .Should().NotContain(typeof(IBudgetRepository),
                because: "Finance must not reach into Budget's repository — read through IBudgetService only");
    }

    [HumansFact]
    public void HoldedSyncService_TakesVendorConnectorInterface()
    {
        var ctor = typeof(HoldedSyncService).GetConstructors().Single();
        ctor.GetParameters().Select(p => p.ParameterType).Should().Contain(typeof(IHoldedClient));
    }

    [HumansFact]
    public void IHoldedRepository_LivesInApplicationInterfacesRepositoriesNamespace()
    {
        typeof(IHoldedRepository).Namespace.Should().Be("Humans.Application.Interfaces.Repositories");
    }

    [HumansFact]
    public void HoldedRepository_IsSealed()
    {
        typeof(HoldedRepository).IsSealed.Should().BeTrue();
    }

    [HumansFact]
    public void HoldedRepository_ImplementsIHoldedRepository()
    {
        typeof(IHoldedRepository).IsAssignableFrom(typeof(HoldedRepository)).Should().BeTrue();
    }
}
