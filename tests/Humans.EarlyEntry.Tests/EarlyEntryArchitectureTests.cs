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
    public void SectionAssemblyDoesNotReferenceEntityFrameworkCore()
    {
        // Early Entry owns no tables — every grant is derived from its providers.
        // Without an EF reference it can't even name a DbContext. Checking the reference
        // catches the section gaining one; a constructor check would not.
        typeof(Section).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .Should().NotContain("Microsoft.EntityFrameworkCore",
                because: "Early Entry derives every grant from its providers and owns no tables");
    }

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
