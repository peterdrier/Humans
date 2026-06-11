using System.Globalization;
using Humans.Application.Csv;
using Humans.Application.Extensions;
using Humans.Application.Services.Cantina.Dtos;

namespace Humans.Web.Cantina;

/// <summary>
/// Renders a <see cref="WeeklyRosterDto"/> as a UTF-8 CSV byte payload.
/// Used by <c>CantinaController.Csv</c> (feature #36 —
/// docs/features/cantina/daily-roster.md). Quoting (RFC 4180 conditional) and
/// OWASP CSV-injection escaping come from the shared <see cref="HumansCsv"/>
/// conventions — source text includes user-controlled profile fields
/// (BurnerName, AllergyOtherText, IntoleranceOtherText). Multi-select fields
/// (allergies, intolerances) are joined with <c>", "</c> into a single cell.
/// The <c>ArrivesOn</c> column is a single ISO date label (e.g. "2026-05-27");
/// the <c>NoShift</c> column lists ISO date labels comma-and-space-
/// separated (empty when the human has a scheduled shift every day of the week).
///
/// Layout (top to bottom):
///   1. "Week of &lt;yyyy-MM-dd&gt; – &lt;yyyy-MM-dd&gt;" header line
///      (skipped when no active event).
///   2. Per-day summary table: Date,On site,Unanswered (7 rows).
///   3. Blank separator row.
///   4. Per-person rows: Name,ArrivesOn,NoShift,Dietary,Allergies,
///      AllergyOther,Intolerances,IntoleranceOther.
/// </summary>
public static class CantinaRosterCsvWriter
{
    public static byte[] Write(WeeklyRosterDto roster)
    {
        ArgumentNullException.ThrowIfNull(roster);

        return HumansCsv.WriteBytes(csv =>
        {
            // ---- Section 1: per-day summary header ----
            if (roster.WeekStartDate is not null && roster.WeekEndDate is not null)
            {
                csv.WriteRow(string.Format(
                    CultureInfo.InvariantCulture,
                    "Week of {0} – {1}",
                    roster.WeekStartDate.Value.ToInvariantDate(),
                    roster.WeekEndDate.Value.ToInvariantDate()));
            }
            else
            {
                csv.WriteRow("Week (no active event)");
            }

            csv.WriteRow("Date", "On site", "Unanswered");
            foreach (var d in roster.Days)
            {
                // No active event — render the day-offset so coordinators
                // can still tell rows apart.
                var dateCol = d.CalendarDate is { } cd
                    ? cd.ToInvariantDate()
                    : string.Format(CultureInfo.InvariantCulture, "Day {0}", d.DayOffset);
                csv.WriteRow(dateCol, d.TotalOnSite, d.UnansweredOnDay);
            }

            // ---- blank separator ----
            csv.NextRecord();

            // ---- Section 2: per-person rows ----
            csv.WriteRow("Name", "ArrivesOn", "NoShift", "Dietary", "Allergies", "AllergyOther", "Intolerances", "IntoleranceOther");
            foreach (var p in roster.People)
            {
                csv.WriteRow(
                    p.BurnerName,
                    p.ArrivesOn.ToInvariantDate(),
                    FormatDayList(p.NoShift),
                    p.DietaryPreference ?? string.Empty,
                    string.Join(", ", p.Allergies),
                    p.AllergyOtherText ?? string.Empty,
                    string.Join(", ", p.Intolerances),
                    p.IntoleranceOtherText ?? string.Empty);
            }
        });
    }

    private static string FormatDayList(IReadOnlyList<NodaTime.LocalDate> days)
    {
        if (days.Count == 0)
            return string.Empty;
        var parts = new string[days.Count];
        for (var i = 0; i < days.Count; i++)
            parts[i] = days[i].ToInvariantDate();
        return string.Join(", ", parts);
    }
}
