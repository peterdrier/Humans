using AwesomeAssertions;
using Humans.EarlyEntry.Contracts;
using Humans.EarlyEntry.Services;
using Microsoft.Extensions.Localization;

namespace Humans.EarlyEntry.Tests;

/// <summary>
/// Architecture tests enforcing the section shape for Early Entry
/// (nobodies-collective/Humans#866, G5 lane 4b-2b).
/// </summary>
public class EarlyEntryArchitectureTests
{

    [HumansFact]
    public void OrchestratorInjectsOnlyTheProviderFanout()
    {
        // The hard rules' orchestrator clause: "Some services are orchestrators, organizing
        // calls to multiple services. These should not call repositories." The fan-out is the
        // whole dependency list — anything else here would be the section growing a data path.
        var paramTypes = typeof(EarlyEntryService).GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().BeEquivalentTo([typeof(IEnumerable<IEarlyEntryProvider>)]);
    }

    [HumansFact]
    public void CachingDecoratorInjectsNoInnerServiceDirectly()
    {
        // peters-hard-rules.md: a CachingDecorator may not call a repository, and must reach
        // the inner service through the interface. Here the Singleton decorator resolves the
        // Scoped inner service per call off the keyed registration, so its constructor takes a
        // scope factory and a logger and nothing else — injecting IEarlyEntryService directly
        // would self-resolve onto the decorator's own unkeyed registration.
        var paramTypes = typeof(CachingEarlyEntryService).GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().NotContain(typeof(IEarlyEntryService));
        paramTypes.Should().NotContain(
            t => t.Name.EndsWith("Repository", StringComparison.Ordinal));
    }
}
