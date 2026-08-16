using AwesomeAssertions;
using Humans.Users.Services;

namespace Humans.Users.Tests.Architecture;

/// <summary>
/// Architecture invariants for the lifecycle state-machine extracted from
/// <c>OnboardingService</c> in nobodies-collective#583. Same shape as the
/// onboarding orchestrator: owns no tables, depends only on cross-section
/// service interfaces, no repository dependencies.
/// </summary>
public class HumanLifecycleArchitectureTests
{
    [HumansFact]
    public void HumanLifecycleService_DependsOnlyOnServiceInterfaces()
    {
        var ctor = typeof(HumanLifecycleService).GetConstructors().Single();
        var forbidden = ctor.GetParameters()
            .Where(p => !p.ParameterType.IsInterface)
            .ToList();

        forbidden.Should().BeEmpty(
            because: "every HumanLifecycleService dependency must be an interface to preserve its orchestrator shape");
    }
}
