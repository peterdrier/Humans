using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Repositories.Shifts;
using Microsoft.EntityFrameworkCore;
using Xunit;
using GeneralAvailabilityService = Humans.Application.Services.Shifts.GeneralAvailabilityService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 repository pattern for
/// <c>GeneralAvailabilityService</c> — the first of the three Shifts-section
/// services to migrate (issue #541, sub-task c).
///
/// <para>
/// General Availability chose <b>Option A</b> (no caching decorator, no dict
/// cache). Small admin/self-service surface, no hot bulk-read path — same
/// rationale used by Users (#243), Governance (#242), Budget (#544), City
/// Planning (#543), and Audit Log (#552) when they skipped the decorator.
/// </para>
/// </summary>
public partial class ArchitectureShapeTests
{
    [HumansFact]
    public void GeneralAvailabilityArchitecture_contracts_hold()
    {
        GeneralAvailabilityService_LivesInHumansApplicationServicesShiftsNamespace();
        GeneralAvailabilityService_HasNoDbContextConstructorParameter();
        GeneralAvailabilityService_HasNoIMemoryCacheConstructorParameter();
        GeneralAvailabilityService_TakesRepository();
        GeneralAvailabilityService_ConstructorTakesNoStoreType();
        IGeneralAvailabilityRepository_LivesInApplicationInterfacesRepositoriesNamespace();
        GeneralAvailabilityRepository_IsSealed();
    }

    // ── GeneralAvailabilityService ──────────────────────────────────────────
    public void GeneralAvailabilityService_LivesInHumansApplicationServicesShiftsNamespace()
    {
        typeof(GeneralAvailabilityService).Namespace
            .Should().Be("Humans.Application.Services.Shifts",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }
    public void GeneralAvailabilityService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(GeneralAvailabilityService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — use IGeneralAvailabilityRepository instead (design-rules §3)");
    }
    public void GeneralAvailabilityService_HasNoIMemoryCacheConstructorParameter()
    {
        var ctor = typeof(GeneralAvailabilityService).GetConstructors().Single();
        var cachingParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Microsoft.Extensions.Caching.Memory", StringComparison.Ordinal));

        cachingParam.Should().BeNull(
            because: "canonical availability data is not IMemoryCache-backed; §15 Option A applies (no caching decorator warranted)");
    }
    public void GeneralAvailabilityService_TakesRepository()
    {
        var ctor = typeof(GeneralAvailabilityService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IGeneralAvailabilityRepository));
    }
    public void GeneralAvailabilityService_ConstructorTakesNoStoreType()
    {
        var ctor = typeof(GeneralAvailabilityService).GetConstructors().Single();
        var storeParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.Namespace ?? string.Empty)
                .StartsWith("Humans.Application.Interfaces.Stores", StringComparison.Ordinal));

        storeParam.Should().BeNull(
            because: "Application services must not depend on store abstractions (design-rules §15); General Availability Option A does not use a store at all");
    }

    // ── IGeneralAvailabilityRepository ──────────────────────────────────────
    public void IGeneralAvailabilityRepository_LivesInApplicationInterfacesRepositoriesNamespace()
    {
        typeof(IGeneralAvailabilityRepository).Namespace
            .Should().Be("Humans.Application.Interfaces.Repositories",
                because: "repository interfaces live in Humans.Application.Interfaces.Repositories per design-rules §3");
    }
    public void GeneralAvailabilityRepository_IsSealed()
    {
        var repoType = typeof(GeneralAvailabilityRepository);

        repoType.IsSealed.Should().BeTrue(
            because: "repository implementations are sealed to prevent ad-hoc extension; any new behavior belongs on the interface");
    }
}
