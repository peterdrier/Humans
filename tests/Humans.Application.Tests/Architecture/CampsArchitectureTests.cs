using AwesomeAssertions;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Microsoft.EntityFrameworkCore;
using Xunit;
using CampService = Humans.Application.Services.Camps.CampService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 repository pattern for the Camps
/// section — migrated per issue #542. Pins the invariants:
/// CampService lives in Application, goes through ICampRepository, and
/// never injects DbContext or reaches directly into the Users domain.
/// Camps did not get a caching decorator (per the §15 recommendation for
/// sections where short-TTL IMemoryCache suffices — see design-rules §15f).
/// </summary>
public class CampsArchitectureTests
{
    // ── CampService ──────────────────────────────────────────────────────────

    [Fact]
    public void CampService_LivesInHumansApplicationServicesCampsNamespace()
    {
        typeof(CampService).Namespace
            .Should().Be("Humans.Application.Services.Camps",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [Fact]
    public void CampService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(CampService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — use ICampRepository instead (design-rules §3)");
    }

    [Fact]
    public void CampService_TakesRepository()
    {
        var ctor = typeof(CampService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(ICampRepository));
    }

    [Fact]
    public void CampService_TakesUserService()
    {
        var ctor = typeof(CampService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IUserService),
            because: "lead display names are resolved via IUserService cross-section (design-rules §6, §9); CampLead.User nav is stripped");
    }

    [Fact]
    public void CampService_TakesImageStorage()
    {
        var ctor = typeof(CampService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(ICampImageStorage),
            because: "filesystem I/O is delegated to an infrastructure abstraction — the Application project can't touch System.IO directly (design-rules §1)");
    }

    [Fact]
    public void CampService_ConstructorTakesNoStoreType()
    {
        var ctor = typeof(CampService).GetConstructors().Single();
        var storeParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Humans.Application.Interfaces.Stores", StringComparison.Ordinal));

        storeParam.Should().BeNull(
            because: "Camps §15 migration does not use a store — IMemoryCache in-service is sufficient (§15f)");
    }

    // ── ICampRepository ──────────────────────────────────────────────────────

    [Fact]
    public void ICampRepository_LivesInApplicationInterfacesRepositoriesNamespace()
    {
        typeof(ICampRepository).Namespace
            .Should().Be("Humans.Application.Interfaces.Repositories",
                because: "repository interfaces live in Humans.Application.Interfaces.Repositories per design-rules §3");
    }

    [Fact]
    public void CampRepository_IsSealed()
    {
        var repoType = typeof(Humans.Infrastructure.Repositories.CampRepository);

        repoType.IsSealed.Should().BeTrue(
            because: "repository implementations are sealed to prevent ad-hoc extension; any new behavior belongs on the interface");
    }

    // ── CampLead ─────────────────────────────────────────────────────────────

    [Fact]
    public void CampLead_HasNoUserNavigationProperty()
    {
        typeof(Humans.Domain.Entities.CampLead)
            .GetProperty("User")
            .Should().BeNull(
                because: "CampLead.User is a cross-domain nav into the Users section; resolve via IUserService instead (design-rules §6c)");
    }

    [Fact]
    public void CampLead_KeepsUserIdForeignKey()
    {
        typeof(Humans.Domain.Entities.CampLead)
            .GetProperty("UserId")
            .Should().NotBeNull(
                because: "FK stays — only the navigation property is stripped");
    }
}
