using AwesomeAssertions;
using Humans.Shifts.Contracts;
using Xunit;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// The T-09 ratchet (issue #720): the agent surface reads user signups through the
/// cached <see cref="IShiftView"/>, not through the Shifts section's own signup service.
/// </summary>
/// <remarks>
/// The rest of <c>ShiftViewArchitectureTests</c> moved into
/// <c>tests/Humans.Shifts.Tests/Architecture</c> at the section's G5
/// (nobodies-collective/Humans#866). This half stayed because it is about
/// <em>Agent's</em> constructors, and resolving them needs
/// <c>SectionDiscoveryExtensions</c>, which lives in Humans.Web — a reference a section
/// test project does not have.
/// </remarks>
public class AgentShiftViewReadTests
{
    // The agent surface used to call IShiftSignupService.GetByUserAsync on
    // every snapshot / get_shift_details turn. T-09 (issue #720) migrated
    // those reads to the cached IShiftView. Pin the dependency direction so
    // the next refactor can't quietly regress the hot path.
    // Both types are internal to Humans.Agent since its G5 move, so they are resolved by
    // reflection rather than named with typeof — the sweep must widen with the section,
    // not quietly stop covering it.
    public static TheoryData<Type> AgentTypesThatReadShiftSignups =>
    [
        SectionType("Humans.Agent.Services.AgentUserSnapshotProvider"),
        SectionType("Humans.Agent.Services.AgentToolDispatcher"),
    ];

    private static Type SectionType(string fullName) =>
        Web.Extensions.SectionDiscoveryExtensions.SectionAssemblies()
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
