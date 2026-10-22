using AwesomeAssertions;
using Humans.Shifts.Contracts;
using Xunit;

namespace Humans.Web.Tests.Architecture;

/// <summary>
/// The agent reads user signups through the cached <see cref="IShiftView"/>, not the Shifts signup service: the read
/// runs on every snapshot and tool turn. Lives here because resolving Agent's internal types needs
/// <c>SectionDiscoveryExtensions</c> from Humans.Web.
/// </summary>
public class AgentShiftViewReadTests
{
    public static TheoryData<Type> AgentTypesThatReadShiftSignups =>
    [
        SectionType("Humans.Agent.Services.AgentUserSnapshotProvider"),
        SectionType("Humans.Agent.Services.AgentToolDispatcher"),
    ];

    private static Type SectionType(string fullName) =>
        Extensions.SectionDiscoveryExtensions.SectionAssemblies()
            .Select(a => a.GetType(fullName, throwOnError: false))
            .FirstOrDefault(t => t is not null)
        ?? throw new InvalidOperationException(
            $"{fullName} not found in any section assembly — did the section move or rename it?");

    [HumansTheory]
    [MemberData(nameof(AgentTypesThatReadShiftSignups))]
    public void Agent_signup_readers_take_IShiftView(Type agentType)
    {
        var ctor = agentType.GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IShiftView),
            because: $"T-09 (issue #720): {agentType.Name} reads user signups from the cached IShiftView");
    }
}
