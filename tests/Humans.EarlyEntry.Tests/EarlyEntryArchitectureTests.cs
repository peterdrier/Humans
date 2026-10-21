using System.Reflection;
using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.EarlyEntry.Contracts;
using Humans.EarlyEntry.Controllers;
using Humans.EarlyEntry.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.EarlyEntry.Tests;

/// <summary>Pins the section's orchestrator shape.</summary>
public class EarlyEntryArchitectureTests
{
    [HumansFact]
    public void OrchestratorInjectsOnlyTheProviderFanout()
    {
        // Exact list, not a subset: a second constructor parameter is this section
        // growing a data path.
        var paramTypes = typeof(EarlyEntryService).GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().BeEquivalentTo([typeof(IEnumerable<IEarlyEntryProvider>)]);
    }

    [HumansFact]
    public void SectionRegistersOnlyTheUncachedOrchestrator()
    {
        // No cache, so no eviction surface: writers in other sections never call in here.
        var services = new ServiceCollection();
        new Section().Register(services, new ConfigurationBuilder().Build());

        var registration = services.Should().ContainSingle().Subject;
        registration.ServiceType.Should().Be(typeof(IEarlyEntryService));
        registration.ImplementationType.Should().Be(typeof(EarlyEntryService));
        registration.Lifetime.Should().Be(ServiceLifetime.Scoped);
    }

    [HumansFact]
    public void RosterRequiresShiftDashboardAccess()
    {
        var authorize = typeof(EarlyEntryRosterController).GetCustomAttribute<AuthorizeAttribute>();

        authorize.Should().NotBeNull();
        authorize.Policy.Should().Be(PolicyNames.ShiftDashboardAccess);
    }
}

