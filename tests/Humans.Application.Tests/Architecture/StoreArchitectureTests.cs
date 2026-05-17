using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Repositories.Store;
using StoreService = Humans.Application.Services.Store.StoreService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests pinning the §15 repository pattern for the Store
/// section. Mirrors <see cref="TeamsArchitectureTests"/> /
/// <see cref="ShiftManagementArchitectureTests"/>:
/// <list type="bullet">
///   <item><description><c>StoreService</c> lives in <c>Humans.Application.Services.Store</c>.</description></item>
///   <item><description><c>StoreService</c> goes through <see cref="IStoreRepository"/> — no <c>DbContext</c> ctor parameter.</description></item>
///   <item><description><c>StoreService</c> is in <c>Humans.Application</c>, which structurally cannot import EF Core (project-graph enforced).</description></item>
///   <item><description><see cref="IStoreRepository"/> has a sealed EF-backed implementation in <c>Humans.Infrastructure.Repositories.Store</c>.</description></item>
/// </list>
/// </summary>
public class StoreArchitectureTests
{
    // ── StoreService ─────────────────────────────────────────────────────────

    [HumansFact]
    public void StoreService_TakesRepository()
    {
        var ctor = typeof(StoreService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IStoreRepository),
            because: "§15 requires every section service to go through its owning repository interface");
    }

    [HumansFact]
    public void StoreService_LivesInApplicationServicesStoreNamespace()
    {
        typeof(StoreService).Namespace
            .Should().Be("Humans.Application.Services.Store",
                because: "section services live under Humans.Application.Services.<Section> per design-rules §15");
    }

    [HumansFact]
    public void StoreService_AssemblyIsHumansApplication()
    {
        typeof(StoreService).Assembly.GetName().Name
            .Should().Be("Humans.Application",
                because: "the Application-layer project graph structurally forbids EF Core references, so services in this assembly cannot import EF even if a future typo tries");
    }

    [HumansFact]
    public void StoreService_DoesNotReferenceEntityFrameworkCore()
    {
        var apiAssembly = typeof(StoreService).Assembly;
        var referencedAssemblies = apiAssembly.GetReferencedAssemblies();

        referencedAssemblies
            .Should().NotContain(
                a => string.Equals(a.Name, "Microsoft.EntityFrameworkCore", StringComparison.Ordinal),
                because: "services in Humans.Application must not import EF Core (design-rules §2b)");
    }

    // ── IStoreRepository + StoreRepository ───────────────────────────────────

    [HumansFact]
    public void StoreRepository_IsSealed()
    {
        typeof(StoreRepository).IsSealed.Should().BeTrue(
            because: "repository implementations are sealed to prevent ad-hoc extension; any new behavior belongs on the interface");
    }

    [HumansFact]
    public void StoreRepository_ImplementsIStoreRepository()
    {
        typeof(IStoreRepository).IsAssignableFrom(typeof(StoreRepository))
            .Should().BeTrue();
    }

    [HumansFact]
    public void StoreRepository_LivesInInfrastructureRepositoriesStoreNamespace()
    {
        typeof(StoreRepository).Namespace
            .Should().Be("Humans.Infrastructure.Repositories.Store",
                because: "EF-backed repository implementations live in Humans.Infrastructure.Repositories.<Section> per design-rules §15b");
    }
}
