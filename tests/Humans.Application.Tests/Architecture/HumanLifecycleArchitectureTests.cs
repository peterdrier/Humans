using AwesomeAssertions;
using HumanLifecycleService = Humans.Application.Services.HumanLifecycle.HumanLifecycleService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture invariants for the lifecycle state-machine extracted from
/// <c>OnboardingService</c> in nobodies-collective#583. Same shape as the
/// onboarding orchestrator: owns no tables, depends only on cross-section
/// service interfaces, no repository dependencies.
/// </summary>
public class HumanLifecycleArchitectureTests
{
    [HumansFact]
    public void HumanLifecycleService_HasNoRepositoryDependency()
    {
        var ctor = typeof(HumanLifecycleService).GetConstructors().Single();
        var repositoryParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Humans.Application.Interfaces.Repositories", StringComparison.Ordinal));

        repositoryParam.Should().BeNull(
            because: "lifecycle owns no tables — it must not inject repository interfaces, only section service interfaces (design-rules §9)");
    }

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
