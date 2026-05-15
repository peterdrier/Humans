using AwesomeAssertions;
using Humans.Application.Interfaces.Onboarding;
using Microsoft.EntityFrameworkCore;
using OnboardingService = Humans.Application.Services.Onboarding.OnboardingService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests for the Onboarding section — migrated to the §15
/// pattern in issue #553. Onboarding is a pure orchestrator (owns no tables),
/// so the enforcement here is tighter than a section with its own
/// repository: no DbContext, no DbSet, no caching, no repository — the
/// constructor must only take cross-section service interfaces.
/// </summary>
public class OnboardingArchitectureTests
{
    [HumansFact]
    public void OnboardingService_HasNoDbSetConstructorParameter()
    {
        var ctor = typeof(OnboardingService).GetConstructors().Single();
        var dbSetParam = ctor.GetParameters()
            .FirstOrDefault(p =>
                p.ParameterType.IsGenericType &&
                string.Equals(
                    p.ParameterType.GetGenericTypeDefinition().FullName,
                    typeof(DbSet<>).FullName,
                    StringComparison.Ordinal));

        dbSetParam.Should().BeNull(
            because: "no DbSet of any kind belongs in the orchestrator — all data access goes through owning section services");
    }

    [HumansFact]
    public void OnboardingService_HasNoIMemoryCacheConstructorParameter()
    {
        var ctor = typeof(OnboardingService).GetConstructors().Single();
        var cachingParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Microsoft.Extensions.Caching.Memory", StringComparison.Ordinal));

        cachingParam.Should().BeNull(
            because: "Onboarding owns no cached data; cache invalidation is owned by each section's write path (design-rules §2d)");
    }

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
    public void OnboardingService_ImplementsIOnboardingEligibilityQuery()
    {
        typeof(IOnboardingEligibilityQuery).IsAssignableFrom(typeof(OnboardingService))
            .Should().BeTrue(
                because: "OnboardingService exposes the narrow IOnboardingEligibilityQuery surface so ProfileService / ConsentService can break the DI cycle with OnboardingService");
    }

    [HumansFact]
    public void IOnboardingService_ExtendsIOnboardingEligibilityQuery()
    {
        typeof(IOnboardingEligibilityQuery).IsAssignableFrom(typeof(IOnboardingService))
            .Should().BeTrue(
                because: "the narrow consent-check query surface is available to every IOnboardingService caller as well as the DI-cycle-break callers");
    }

    [HumansFact]
    public void OnboardingService_DependsOnlyOnServiceInterfaces()
    {
        var ctor = typeof(OnboardingService).GetConstructors().Single();
        var forbidden = ctor.GetParameters()
            .Where(p => p.ParameterType != typeof(NodaTime.IClock))
            .Where(p =>
                // Services are interfaces under Humans.Application.Interfaces.*
                // (IProfileService, IUserService, IApplicationDecisionService, ...)
                // plus well-known cross-cuts (ILogger, IMetrics, ...).
                !p.ParameterType.IsInterface)
            .ToList();

        forbidden.Should().BeEmpty(
            because: "every OnboardingService dependency must be an interface to preserve its orchestrator shape");
    }
}
