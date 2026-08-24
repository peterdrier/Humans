using AwesomeAssertions;
using Humans.EarlyEntry.Contracts;
using Humans.EarlyEntry.Services;

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
}
