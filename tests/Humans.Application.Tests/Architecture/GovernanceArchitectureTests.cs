using System.Reflection;
using AwesomeAssertions;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Stores;
using Humans.Application.Services.Governance;
using Humans.Infrastructure.Services.Governance;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the repository/store/decorator pattern for the
/// Governance section — the first section migrated per PR #503 /
/// <c>docs/superpowers/plans/2026-04-15-governance-migration.md</c>.
///
/// These tests are the enforcement mechanism for §§3–5 of
/// <c>docs/architecture/design-rules.md</c> applied to Governance: if any
/// future change drags the service back into <c>Humans.Infrastructure</c>,
/// reintroduces a <c>DbContext</c> dependency, or accidentally pulls an EF
/// Core reference into <c>Humans.Application</c>, these tests fail loudly.
/// </summary>
public class GovernanceArchitectureTests
{
    [Fact]
    public void ApplicationDecisionService_LivesInHumansApplicationServicesGovernanceNamespace()
    {
        typeof(ApplicationDecisionService).Namespace
            .Should().Be("Humans.Application.Services.Governance",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [Fact]
    public void ApplicationDecisionService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(ApplicationDecisionService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — use IApplicationRepository instead (design-rules §3)");
    }

    [Fact]
    public void ApplicationDecisionService_HasNoIMemoryCacheConstructorParameter()
    {
        var ctor = typeof(ApplicationDecisionService).GetConstructors().Single();
        var cachingParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Microsoft.Extensions.Caching.Memory", StringComparison.Ordinal));

        cachingParam.Should().BeNull(
            because: "caching is the decorator's concern (design-rules §5), not the service's");
    }

    [Fact]
    public void ApplicationDecisionService_TakesRepositoryAndStore()
    {
        var ctor = typeof(ApplicationDecisionService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IApplicationRepository));
        paramTypes.Should().Contain(typeof(IApplicationStore));
    }

    [Fact]
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

    [Fact]
    public void IApplicationRepository_LivesInApplicationInterfacesRepositoriesNamespace()
    {
        typeof(IApplicationRepository).Namespace
            .Should().Be("Humans.Application.Interfaces.Repositories",
                because: "repository interfaces live in Humans.Application.Interfaces.Repositories per design-rules §3");
    }

    [Fact]
    public void IApplicationStore_LivesInApplicationInterfacesStoresNamespace()
    {
        typeof(IApplicationStore).Namespace
            .Should().Be("Humans.Application.Interfaces.Stores",
                because: "store interfaces live in Humans.Application.Interfaces.Stores per design-rules §4");
    }

    [Fact]
    public void CachingApplicationDecisionService_LivesInHumansInfrastructureServicesGovernanceNamespace()
    {
        typeof(CachingApplicationDecisionService).Namespace
            .Should().Be("Humans.Infrastructure.Services.Governance",
                because: "caching decorators live in Humans.Infrastructure.Services.{Section} alongside the IMemoryCache-backed invalidators they wrap (design-rules §5)");
    }
}
