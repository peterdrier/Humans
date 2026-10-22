using System.Globalization;
using Humans.Base.Csv;
using Humans.Base.Extensions;
using Humans.Cantina.Services.Dtos;

namespace Humans.Cantina.Models;

/// <summary>Multi-select fields are joined with <c>", "</c> into one cell.</summary>
internal static class CantinaRosterCsvWriter
{
    public static byte[] Write(WeeklyRosterDto roster)
    {
        ArgumentNullException.ThrowIfNull(roster);

        return HumansCsv.WriteBytes(csv =>
        {
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
                // No active event: the day offset still tells rows apart.
                var dateCol = d.CalendarDate is { } cd
                    ? cd.ToInvariantDate()
                    : string.Format(CultureInfo.InvariantCulture, "Day {0}", d.DayOffset);
                csv.WriteRow(dateCol, d.TotalOnSite, d.UnansweredOnDay);
            }

            csv.NextRecord();

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
