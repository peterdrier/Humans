using Humans.Application.Interfaces;
using Humans.Application.Services.Cantina.Dtos;

namespace Humans.Application.Interfaces.Cantina;

/// <summary>
/// Cross-section read service that powers the Cantina Daily Roster page
/// (feature #36 — docs/features/cantina/daily-roster.md). The controller
/// gets the entire page payload — headers, aggregates, per-human rows —
/// in one call so the view stays free of further service look-ups.
///
/// <para>
/// The service stitches three sources together at the Application
/// boundary so no medical fields cross it: the on-site cohort + their
/// <c>VolunteerEventProfile</c> rows from <c>IShiftManagementRepository</c>,
/// and burner-name labels from <c>IProfileService</c> with a
/// <c>User.DisplayName</c> fallback via <c>IUserService</c>.
/// </para>
/// </summary>
public interface ICantinaRosterService : IApplicationService
{
    /// <summary>
    /// Builds the full Cantina Daily Roster payload for the given
    /// <paramref name="dayOffset"/>. Returns a fully-populated DTO with
    /// zero counts and empty lists when there is no active event or no
    /// on-site humans for the day — the controller treats both as "no
    /// data" copy without branching.
    /// </summary>
    Task<DailyRosterDto> GetDailyRosterAsync(int dayOffset, CancellationToken ct = default);
}
