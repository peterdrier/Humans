using AwesomeAssertions;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Governance;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the repository/service pattern for the
/// Governance section. Governance does not use a caching decorator or
/// in-memory store — the service talks directly to the repository and
/// invalidates cross-cutting caches inline after successful writes.
///
/// These tests fail loudly if a future change drags the service back into
/// <c>Humans.Infrastructure</c>, reintroduces a <c>DbContext</c> dependency,
/// or accidentally pulls an EF Core reference into <c>Humans.Application</c>.
/// </summary>
public class GovernanceArchitectureTests
{
    [HumansFact]
    public void ApplicationDecisionService_LivesInHumansApplicationServicesGovernanceNamespace()
    {
        typeof(ApplicationDecisionService).Namespace
            .Should().Be("Humans.Application.Services.Governance",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [HumansFact]
    public void ApplicationDecisionService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(ApplicationDecisionService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — use IApplicationRepository instead (design-rules §3)");
    }

    [HumansFact]
    public void ApplicationDecisionService_HasNoIMemoryCacheConstructorParameter()
    {
        var ctor = typeof(ApplicationDecisionService).GetConstructors().Single();
        var cachingParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Microsoft.Extensions.Caching.Memory", StringComparison.Ordinal));

        cachingParam.Should().BeNull(
            because: "caching is handled via cross-cutting invalidator interfaces, not IMemoryCache directly");
    }

    [HumansFact]
    public void ApplicationDecisionService_TakesRepository()
    {
        var ctor = typeof(ApplicationDecisionService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IApplicationRepository));
    }

    [HumansFact]
    public void ApplicationDecisionService_TakesNoTypeFromInterfacesStoresNamespace()
    {
        var ctor = typeof(ApplicationDecisionService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => (p.ParameterType.Namespace ?? string.Empty)
                    .StartsWith("Humans.Application.Interfaces.Stores", StringComparison.Ordinal),
                because: "Governance has no store — service reads from IApplicationRepository directly (issue #533)");
    }

    [HumansFact]
    public void HumansApplicationAssembly_HasNoReferenceToEntityFrameworkCore()
    {
        var applicationAssembly = typeof(IApplicationDecisionService).Assembly;

        var referenced = applicationAssembly.GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .ToList();

        referenced.Should().NotContain(
            name => name.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal),
            because: "Humans.Application must not reference EF Core — repositories live in Infrastructure (design-rules §1, §3)");
    }

    [HumansFact]
    public void IApplicationRepository_LivesInApplicationInterfacesRepositoriesNamespace()
    {
        typeof(IApplicationRepository).Namespace
            .Should().Be("Humans.Application.Interfaces.Repositories",
                because: "repository interfaces live in Humans.Application.Interfaces.Repositories per design-rules §3");
    }
}
