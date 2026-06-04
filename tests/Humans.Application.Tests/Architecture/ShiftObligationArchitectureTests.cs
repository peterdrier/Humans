using AwesomeAssertions;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Shifts;
using ShiftObligationService = Humans.Application.Services.Camps.ShiftObligationService;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Architecture tests for the barrio shift-obligation feature (Camps section).
///
/// <para>
/// <b>Table ownership</b> (<c>shift_obligations</c> + <c>camp_season_shift_obligations</c>)
/// is enforced structurally and is deliberately NOT re-asserted here:
/// <list type="bullet">
/// <item>"a table is referenced by exactly one repository" is the HUM0025 analyzer
///   (<c>SingleRepositoryPerTableAnalyzer</c>) — per peters-hard-rules.md, call-site
///   rules that fit the analyzer pattern are owned by the analyzer, not by a baseline
///   test.</item>
/// <item>the ownership map in <see cref="ServiceBoundaryArchitectureTests"/> already
///   binds <see cref="IShiftObligationRepository"/> to the Camps section, and
///   <c>Repository_ownership_map_covers_all_repositories</c> fails if it is missing.</item>
/// </list>
/// What is NOT covered by either is the <b>direction of the cross-section read seam</b>:
/// Camps reads Shifts through <see cref="IShiftServiceRead"/> only, never through a
/// Shifts repository or the full <see cref="IShiftManagementService"/> write surface.
/// That is what this file pins.
/// </para>
/// </summary>
public class ShiftObligationArchitectureTests
{
    [HumansFact]
    public void ShiftObligationService_ReachesShiftsOnlyThroughIShiftServiceRead()
    {
        var ctor = typeof(ShiftObligationService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IShiftServiceRead),
            because: "Camps reads Shifts data (rota/signup counts) through the narrow cross-section read interface");

        paramTypes.Should().NotContain(typeof(IShiftManagementService),
            because: "Camps must not take the full Shifts write surface — cross-section access is read-only via IShiftServiceRead");
        paramTypes.Should().NotContain(typeof(IShiftManagementRepository),
            because: "a section never injects another section's repository (peters-hard-rules.md); Shifts data comes via IShiftServiceRead");
        paramTypes.Should().NotContain(typeof(IVolunteerTrackingRepository),
            because: "a section never injects another section's repository (peters-hard-rules.md); Shifts data comes via IShiftServiceRead");
    }

    [HumansFact]
    public void ShiftObligationService_OwnsObligationWritesThroughCampsRepository()
    {
        var ctor = typeof(ShiftObligationService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();

        paramTypes.Should().Contain(typeof(IShiftObligationRepository),
            because: "obligation config + per-season overrides (shift_obligations, camp_season_shift_obligations) are written through the section-owned repository, not through ICampRepository");
    }
}
