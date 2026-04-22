using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Services.Governance;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests pinning the §15 orchestrator shape for
/// <see cref="MembershipCalculator"/>. The calculator owns no tables — it
/// stitches profile + team + role + consent data through other sections'
/// service interfaces. These tests fail loudly if a future change reintroduces
/// a <c>DbContext</c> dependency, an <c>IDbContextFactory</c>, or drags the
/// service back into <c>Humans.Infrastructure</c>.
///
/// <para>
/// See <c>docs/architecture/design-rules.md</c> §15 (Profile-section canonical
/// cache-collapse architecture) and §15i (known migrations). Part of issue
/// #559 (Governance §15 Part 1 — MembershipCalculator).
/// </para>
/// </summary>
public class MembershipCalculatorArchitectureTests
{
    [Fact]
    public void MembershipCalculator_LivesInHumansApplicationServicesGovernanceNamespace()
    {
        typeof(MembershipCalculator).Namespace
            .Should().Be("Humans.Application.Services.Governance",
                because: "orchestrators with business logic live in Humans.Application per design-rules §2b, organized by section — MembershipCalculator reads belong under Governance alongside ApplicationDecisionService");
    }

    [Fact]
    public void MembershipCalculator_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(MembershipCalculator).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — cross-section reads go through owning service interfaces per design-rules §9");
    }

    [Fact]
    public void MembershipCalculator_HasNoDbContextFactoryConstructorParameter()
    {
        var ctor = typeof(MembershipCalculator).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => (p.ParameterType.FullName ?? string.Empty)
                    .StartsWith("Microsoft.EntityFrameworkCore.IDbContextFactory", StringComparison.Ordinal),
                because: "MembershipCalculator is a pure orchestrator — it owns no tables and must not hold an IDbContextFactory");
    }

    [Fact]
    public void MembershipCalculator_HasNoRepositoryConstructorParameter()
    {
        // The orchestrator owns no tables; it must not inject any
        // IXxxRepository either. All cross-section reads go through
        // service interfaces per design-rules §9.
        var ctor = typeof(MembershipCalculator).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => (p.ParameterType.Namespace ?? string.Empty)
                    .StartsWith("Humans.Application.Interfaces.Repositories", StringComparison.Ordinal),
                because: "MembershipCalculator owns no data — it must read only through other sections' service interfaces");
    }

    [Fact]
    public void MembershipCalculator_TakesProfileService()
    {
        var ctor = typeof(MembershipCalculator).GetConstructors().Single();
        ctor.GetParameters().Select(p => p.ParameterType)
            .Should().Contain(typeof(IProfileService),
                because: "profile reads go through IProfileService per design-rules §9");
    }

    [Fact]
    public void MembershipCalculator_TakesMembershipQuery()
    {
        var ctor = typeof(MembershipCalculator).GetConstructors().Single();
        ctor.GetParameters().Select(p => p.ParameterType)
            .Should().Contain(typeof(IMembershipQuery),
                because: "team + role reads go through IMembershipQuery (a thin pass-through over ITeamService and IRoleAssignmentService) to break the circular DI graph caused by ISystemTeamSync — see PR #279");
    }

    [Fact]
    public void MembershipCalculator_DoesNotTakeTeamServiceDirectly()
    {
        var ctor = typeof(MembershipCalculator).GetConstructors().Single();
        ctor.GetParameters().Select(p => p.ParameterType)
            .Should().NotContain(typeof(ITeamService),
                because: "injecting ITeamService directly closes the DI cycle ITeamService -> ISystemTeamSync -> IMembershipCalculator — use IMembershipQuery instead");
    }

    [Fact]
    public void MembershipCalculator_DoesNotTakeRoleAssignmentServiceDirectly()
    {
        var ctor = typeof(MembershipCalculator).GetConstructors().Single();
        ctor.GetParameters().Select(p => p.ParameterType)
            .Should().NotContain(typeof(IRoleAssignmentService),
                because: "injecting IRoleAssignmentService directly closes the DI cycle IRoleAssignmentService -> ISystemTeamSync -> IMembershipCalculator — use IMembershipQuery instead");
    }

    [Fact]
    public void MembershipCalculator_TakesUserService()
    {
        var ctor = typeof(MembershipCalculator).GetConstructors().Single();
        ctor.GetParameters().Select(p => p.ParameterType)
            .Should().Contain(typeof(IUserService),
                because: "user reads (for DeletionRequestedAt in PartitionUsersAsync) go through IUserService per design-rules §9");
    }

    [Fact]
    public void MembershipCalculator_IsSealed()
    {
        typeof(MembershipCalculator).IsSealed
            .Should().BeTrue(
                because: "orchestrators are terminal — no subclass should extend the cross-section stitching logic");
    }
}
