using Humans.Application.Interfaces;
using Humans.Application.Services.Cantina.Dtos;
using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Application.Interfaces.Cantina;

/// <summary>
/// Cross-section read service that powers the Cantina Weekly Roster page
/// (feature #36 — docs/features/cantina/daily-roster.md). The controller
/// gets the entire page payload — headers, weekly aggregates, per-day
/// mini-summary, per-human rows — in one call so the view stays free of
/// further service look-ups.
///
/// <para>
/// The service stitches three sources together at the Application
/// boundary so no medical fields cross it: the on-site cohort + their
/// <c>VolunteerEventProfile</c> rows from <c>IShiftManagementRepository</c>
/// (queried per-day for the 7 days Mon–Sun and unioned by user id), and
/// burner-name labels from <c>IProfileService</c> with a
/// <c>User.DisplayName</c> fallback via <c>IUserService</c>.
/// </para>
/// </summary>
public interface ICantinaRosterService : IApplicationService
{
    /// <summary>
    /// Builds the full Cantina Weekly Roster payload for the week whose
    /// Monday is at <paramref name="weekStartOffset"/> (relative to
    /// <c>EventSettings.GateOpeningDate</c>). Returns a fully-populated
    /// DTO with zero counts and empty lists when there is no active event
    /// or no on-site humans for the week — the controller treats both as
    /// "no data" copy without branching.
    /// </summary>
    Task<WeeklyRosterDto> GetWeeklyRosterAsync(int weekStartOffset, CancellationToken ct = default);

    /// <summary>
    /// Computes the <c>weekStartOffset</c> of the week containing
    /// <paramref name="now"/> in the active event's timezone. Returns the
    /// day-offset of that week's Monday relative to
    /// <c>EventSettings.GateOpeningDate</c>. The controller calls this to
    /// resolve a default when no explicit <c>weekStartOffset</c> is on the
    /// URL.
    /// </summary>
    int GetCurrentWeekStartOffsetForActiveEvent(EventSettings eventSettings, Instant now);

    /// <summary>
    /// Builds the per-day matrix payload for the Cantina Daily Matrix page
    /// (drill-down from the weekly view's per-day mini-table). One row per
    /// unique on-site human on the requested day, plus day-scoped aggregates
    /// (dietary breakdown, allergy + intolerance rollups, "Other" free-text
    /// entries). Returns a fully-populated DTO with zero counts and empty
    /// lists when there is no active event or no on-site humans for the day.
    /// People are returned in unspecified order; display sort is the Web
    /// layer's responsibility (<c>CantinaRosterAssembler.WithSortedPeople</c>).
    /// </summary>
    Task<DailyMatrixDto> GetDailyRosterAsync(int dayOffset, CancellationToken ct = default);

    /// <summary>
    /// Computes the day-offset of "today" in the active event's timezone,
    /// relative to <c>EventSettings.GateOpeningDate</c>. The controller
    /// calls this to resolve a default when no explicit <c>dayOffset</c>
    /// is on the URL.
    /// </summary>
    int GetCurrentDayOffsetForActiveEvent(EventSettings eventSettings, Instant now);
}
