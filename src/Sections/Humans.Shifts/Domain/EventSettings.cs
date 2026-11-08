using NodaTime;

namespace Humans.Shifts.Domain;

/// <summary>
/// Shifts' knobs for one event cycle. Settings owns the calendar and early entry.
/// </summary>
internal sealed class EventSettings
{
    public Guid Id { get; init; }

    // Shifts' own build sub-period starts (day offsets from gate opening). New rows use Shifts' defaults;
    // nullable historical fields also resolve to those defaults.

    public int? SubPeriodFirstCrewStart { get; set; }

    public int? SubPeriodSetupWeekStart { get; set; }

    public int? SubPeriodPreEventWeekStart { get; set; }

    public int? SubPeriodFinishingWeekendStart { get; set; }

    /// <summary>Whether regular volunteers can browse shifts.</summary>
    public bool IsShiftBrowsingOpen { get; set; }

    /// <summary>Optional cap on total volunteer signups per event.</summary>
    public int? GlobalVolunteerCap { get; set; }

    /// <summary>How long before a shift its reminder goes out.</summary>
    public int ReminderLeadTimeHours { get; set; } = 24;

    public Instant CreatedAt { get; init; }

    public Instant UpdatedAt { get; set; }

    public ICollection<Rota> Rotas { get; } = new List<Rota>();
}
