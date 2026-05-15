using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using HumanLifecycleService = Humans.Application.Services.HumanLifecycle.HumanLifecycleService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture invariants for the lifecycle state-machine extracted from
/// <c>OnboardingService</c> in nobodies-collective#583. Same shape as the
/// onboarding orchestrator: owns no tables, depends only on cross-section
/// service interfaces, no DbContext / DbSet / cache / repository
/// dependencies.
/// </summary>
public class HumanLifecycleArchitectureTests
{
    [HumansFact]
    public void HumanLifecycleService_HasNoDbSetConstructorParameter()
    {
        var ctor = typeof(HumanLifecycleService).GetConstructors().Single();
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
    public void HumanLifecycleService_HasNoIMemoryCacheConstructorParameter()
    {
        var ctor = typeof(HumanLifecycleService).GetConstructors().Single();
        var cachingParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Microsoft.Extensions.Caching.Memory", StringComparison.Ordinal));

        cachingParam.Should().BeNull(
            because: "lifecycle owns no cached data; cache invalidation is the responsibility of each owning section's write path (design-rules §2d)");
    }

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
