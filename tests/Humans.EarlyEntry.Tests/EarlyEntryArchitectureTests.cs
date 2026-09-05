using AwesomeAssertions;
using Humans.EarlyEntry.Contracts;
using Humans.EarlyEntry.Services;

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
}
