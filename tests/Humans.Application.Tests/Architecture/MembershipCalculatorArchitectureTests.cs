using AwesomeAssertions;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Services.Governance;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests pinning DI-cycle guards and sealing for
/// <see cref="MembershipCalculator"/>. The calculator routes team + role reads
/// through <c>IMembershipQuery</c> to break the circular DI graph caused by
/// <c>ISystemTeamSync</c>.
///
/// <para>
/// See <c>docs/architecture/design-rules.md</c> §15. Part of issue
/// #559 (Governance §15 Part 1 — MembershipCalculator).
/// </para>
/// </summary>
public class MembershipCalculatorArchitectureTests
{
    [HumansFact]
    public void MembershipCalculator_DoesNotTakeTeamServiceDirectly()
    {
        var ctor = typeof(MembershipCalculator).GetConstructors().Single();
        ctor.GetParameters().Select(p => p.ParameterType)
            .Should().NotContain(typeof(ITeamService),
                because: "injecting ITeamService directly closes the DI cycle ITeamService -> ISystemTeamSync -> IMembershipCalculator — use IMembershipQuery instead");
    }

    [HumansFact]
    public void MembershipCalculator_DoesNotTakeRoleAssignmentServiceDirectly()
    {
        var ctor = typeof(MembershipCalculator).GetConstructors().Single();
        ctor.GetParameters().Select(p => p.ParameterType)
            .Should().NotContain(typeof(IRoleAssignmentService),
                because: "injecting IRoleAssignmentService directly closes the DI cycle IRoleAssignmentService -> ISystemTeamSync -> IMembershipCalculator — use IMembershipQuery instead");
    }

    [HumansFact]
    public void MembershipCalculator_HasNoRepositoryConstructorParameter()
    {
        // The orchestrator owns no tables; it must not inject any
        // IXxxRepository either. All cross-section reads go through
        // service interfaces per design-rules §9. No universal enforcer covers
        // this yet (A2 deferred); HUM0017 only catches cross-section repos.
        var ctor = typeof(MembershipCalculator).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => (p.ParameterType.Namespace ?? string.Empty)
                    .StartsWith("Humans.Application.Interfaces.Repositories", StringComparison.Ordinal),
                because: "MembershipCalculator owns no data — it must read only through other sections' service interfaces");
    }

    [HumansFact]
    public void MembershipCalculator_IsSealed()
    {
        typeof(MembershipCalculator).IsSealed
            .Should().BeTrue(
                because: "orchestrators are terminal — no subclass should extend the cross-section stitching logic");
    }
}
