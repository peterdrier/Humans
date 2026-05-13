using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Repositories.Shifts;
using Microsoft.EntityFrameworkCore;
using Xunit;
using VolunteerTrackingService = Humans.Application.Services.Shifts.VolunteerTrackingService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing the §15 repository pattern for
/// <c>VolunteerTrackingService</c>.
///
/// <para>
/// VolunteerTrackingService chose <b>Option A</b> (no caching decorator). Volunteer
/// tracking reads are coordinator-scoped and change frequently during the build
/// period — caching would produce stale heatmap data. Same rationale used by
/// ShiftSignup (#541), Users (#243), Governance (#242), Feedback (#549), and Auth
/// (#551) when they skipped the decorator.
/// </para>
/// </summary>
public class VolunteerTrackingArchitectureTests
{
    // ── VolunteerTrackingService ────────────────────────────────────────────

    [HumansFact]
    public void VolunteerTrackingService_LivesInHumansApplicationServicesShiftsNamespace()
    {
        typeof(VolunteerTrackingService).Namespace
            .Should().Be("Humans.Application.Services.Shifts",
                because: "services with business logic live in Humans.Application per design-rules §2b, organized by section");
    }

    [HumansFact]
    public void VolunteerTrackingService_HasNoDbContextConstructorParameter()
    {
        var ctor = typeof(VolunteerTrackingService).GetConstructors().Single();
        ctor.GetParameters()
            .Should().NotContain(
                p => typeof(DbContext).IsAssignableFrom(p.ParameterType),
                because: "services in Humans.Application must never take DbContext — use IVolunteerTrackingRepository instead (design-rules §3)");
    }

    [HumansFact]
    public void VolunteerTrackingService_HasNoIMemoryCacheConstructorParameter()
    {
        var ctor = typeof(VolunteerTrackingService).GetConstructors().Single();
        var cachingParam = ctor.GetParameters()
            .FirstOrDefault(p => (p.ParameterType.FullName ?? string.Empty)
                .StartsWith("Microsoft.Extensions.Caching.Memory", StringComparison.Ordinal));

        cachingParam.Should().BeNull(
            because: "volunteer-tracking reads are coordinator-scoped and short-lived; §15 Option A applies (no caching decorator warranted)");
    }

    [HumansFact]
    public void VolunteerTrackingService_TakesRepository()
    {
        var ctor = typeof(VolunteerTrackingService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IVolunteerTrackingRepository));
    }

    // ── IVolunteerTrackingRepository ───────────────────────────────────────

    [HumansFact]
    public void IVolunteerTrackingRepository_LivesInApplicationInterfacesRepositoriesNamespace()
    {
        typeof(IVolunteerTrackingRepository).Namespace
            .Should().Be("Humans.Application.Interfaces.Repositories",
                because: "repository interfaces live in Humans.Application.Interfaces.Repositories per design-rules §3");
    }

    [HumansFact]
    public void VolunteerTrackingRepository_IsSealed()
    {
        var repoType = typeof(VolunteerTrackingRepository);

        repoType.IsSealed.Should().BeTrue(
            because: "repository implementations are sealed to prevent ad-hoc extension; any new behavior belongs on the interface");
    }
}
