using AwesomeAssertions;
using OnboardingService = Humans.Application.Services.Onboarding.OnboardingService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests for the Onboarding section — migrated to the §15
/// pattern in issue #553. Onboarding is a pure orchestrator (owns no tables):
/// no repository — the constructor must only take cross-section service interfaces.
/// </summary>
public class OnboardingArchitectureTests
{
    [HumansFact]
    public void OnboardingService_HasNoRepositoryDependency()
    {
        var ctor = typeof(OnboardingService).GetConstructors().Single();
        var repositoryParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Humans.Application.Interfaces.Repositories", StringComparison.Ordinal));

        repositoryParam.Should().BeNull(
            because: "Onboarding owns no tables — it must not inject repository interfaces, only section service interfaces (design-rules §9)");
    }

    [HumansFact]
    public void OnboardingService_DependsOnlyOnServiceInterfaces()
    {
        var ctor = typeof(OnboardingService).GetConstructors().Single();
        var forbidden = ctor.GetParameters()
            .Where(p => p.ParameterType != typeof(NodaTime.IClock))
            .Where(p =>
                // Services are interfaces under Humans.Application.Interfaces.*
                // (IUserService, IApplicationDecisionService, IAuditLogService, ...)
                // plus well-known cross-cuts (ILogger, IMetrics, ...).
                !p.ParameterType.IsInterface)
            .ToList();

        forbidden.Should().BeEmpty(
            because: "every OnboardingService dependency must be an interface to preserve its orchestrator shape");
    }
}
