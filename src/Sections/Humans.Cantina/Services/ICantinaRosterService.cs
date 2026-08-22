using Humans.Base.Interfaces;
using Humans.Cantina.Services.Dtos;

namespace Humans.Cantina.Services;

/// <summary>
/// Cross-section read service that powers the Cantina Weekly Roster page
/// (feature #36 — src/Sections/Humans.Cantina/Docs/features/daily-roster.md). The controller
/// gets the entire page payload — headers, weekly aggregates, per-day
/// mini-summary, per-human rows — in one call so the view stays free of
/// further service look-ups.
///
/// <para>
/// The service stitches two sources together: the on-site cohort from
/// <c>IShiftManagementServiceRead</c> (queried per day and unioned by user
/// id), and dietary fields plus burner names from <c>IUserServiceRead</c>'s
/// cached profile read-model. Both are section services — Cantina never
/// touches another section's repository.
/// </para>
/// <para>
/// The medical boundary is an exclusion, not a narrower read: the cached
/// profile record Cantina receives does carry <c>MedicalConditions</c>, and
/// this service simply never reads it. Nothing downstream can leak it because
/// the output records have no such property — but the guarantee lives here and
/// in those records, not in the read contract.
/// </para>
/// </summary>
internal interface ICantinaRosterService : IApplicationService
{
    /// <summary>
    /// Builds the full Cantina Weekly Roster payload for the week whose
    /// Monday is at <paramref name="weekStartOffset"/> (relative to
    /// <c>EventSettings.GateOpeningDate</c>). Null means the week containing
    /// today in the active event's timezone, and zero without an active
    /// event. Returns a fully-populated DTO with zero counts and empty lists
    /// when there is no active event or no on-site humans for the week — the
    /// controller treats both as "no data" copy without branching.
    /// </summary>
    Task<WeeklyRosterDto> GetWeeklyRosterAsync(int? weekStartOffset = null, CancellationToken ct = default);

    /// <summary>
    /// Builds the per-day matrix payload for the Cantina Daily Matrix page
    /// (drill-down from the weekly view's per-day mini-table). One row per
    /// unique on-site human on the requested day, plus day-scoped aggregates
    /// (the unanswered headline count). Null means today in the active event's
    /// timezone, and zero without an active event. Returns a fully-populated
    /// DTO with zero counts and empty lists when there is no active event or
    /// no on-site humans for the day. People are returned in unspecified
    /// order; display sort is the Web layer's responsibility
    /// (<c>CantinaRosterAssembler.WithSortedPeople</c>).
    /// </summary>
    Task<DailyMatrixDto> GetDailyRosterAsync(int? dayOffset = null, CancellationToken ct = default);
}
