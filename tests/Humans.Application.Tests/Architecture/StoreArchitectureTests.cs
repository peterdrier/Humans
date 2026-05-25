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
///   <item><description><c>StoreService</c> is in <c>Humans.Application</c>, which structurally cannot import EF Core (project-graph enforced).</description></item>
///   <item><description><see cref="IStoreRepository"/> has a sealed EF-backed implementation in <c>Humans.Infrastructure.Repositories.Store</c>.</description></item>
/// </list>
/// </summary>
public class StoreArchitectureTests
{
    // ── StoreService ─────────────────────────────────────────────────────────

    // TakesRepository check covered by pattern G (positive wiring noise).
    // Service-namespace check covered by HUM0012.
    // AssemblyIsHumansApplication check covered by HUM0012.

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

    // Sealed-repository check covered by IRepositoryImplementationsAreSealedRule.
    // Infrastructure-namespace check covered by RepositoryImplementationsLiveInInfrastructureRule.

    [HumansFact]
    public void StoreRepository_ImplementsIStoreRepository()
    {
        typeof(IStoreRepository).IsAssignableFrom(typeof(StoreRepository))
            .Should().BeTrue();
    }
}
