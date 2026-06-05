using System.Globalization;
using System.Text;
using Humans.Application.Extensions;
using Humans.Application.Services.Cantina.Dtos;

namespace Humans.Web.Cantina;

/// <summary>
/// Renders a <see cref="WeeklyRosterDto"/> as a UTF-8 CSV byte payload.
/// Used by <c>CantinaController.Csv</c> (feature #36 —
/// docs/features/cantina/daily-roster.md). Distinct from
/// <c>Humans.Web.Extensions.CsvExtensions</c>: that helper quotes every
/// field unconditionally; the cantina export follows RFC 4180 conditional
/// quoting so the output stays readable when opened in a spreadsheet
/// without parser warnings. Multi-select fields (allergies, intolerances)
/// are joined with <c>", "</c> into a single cell. The <c>ArrivesOn</c>
/// column is a single ISO date label (e.g. "2026-05-27"); the
/// <c>NoShift</c> column lists ISO date labels comma-and-space-
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
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    public static byte[] Write(WeeklyRosterDto roster)
    {
        ArgumentNullException.ThrowIfNull(roster);

        using var ms = new MemoryStream();
        ms.Write(Utf8Bom, 0, Utf8Bom.Length);
        using (var sw = new StreamWriter(ms, Utf8NoBom, leaveOpen: true) { NewLine = "\r\n" })
        {
            // ---- Section 1: per-day summary header ----
            if (roster.WeekStartDate is not null && roster.WeekEndDate is not null)
            {
                sw.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "Week of {0} – {1}",
                    roster.WeekStartDate.Value.ToInvariantDate(),
                    roster.WeekEndDate.Value.ToInvariantDate()));
            }
            else
            {
                sw.WriteLine("Week (no active event)");
            }
            sw.WriteLine("Date,On site,Unanswered");
            foreach (var d in roster.Days)
            {
                string dateCol;
                if (d.CalendarDate is { } cd)
                {
                    dateCol = cd.ToInvariantDate();
                }
                else
                {
                    // No active event — render the day-offset so coordinators
                    // can still tell rows apart.
                    dateCol = string.Format(CultureInfo.InvariantCulture, "Day {0}", d.DayOffset);
                }
                sw.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0},{1},{2}",
                    Quote(dateCol),
                    d.TotalOnSite,
                    d.UnansweredOnDay));
            }

            // ---- blank separator ----
            sw.WriteLine();

            // ---- Section 2: per-person rows ----
            sw.WriteLine("Name,ArrivesOn,NoShift,Dietary,Allergies,AllergyOther,Intolerances,IntoleranceOther");
            foreach (var p in roster.People)
            {
                sw.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0},{1},{2},{3},{4},{5},{6},{7}",
                    Quote(p.BurnerName),
                    Quote(p.ArrivesOn.ToInvariantDate()),
                    Quote(FormatDayList(p.NoShift)),
                    Quote(p.DietaryPreference ?? string.Empty),
                    Quote(string.Join(", ", p.Allergies)),
                    Quote(p.AllergyOtherText ?? string.Empty),
                    Quote(string.Join(", ", p.Intolerances)),
                    Quote(p.IntoleranceOtherText ?? string.Empty)));
            }
        }

        return ms.ToArray();
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

    // Per OWASP CSV-injection guidance: cells beginning with =, +, -, @, \t, or \r
    // are interpreted as formulas when opened in Excel/LibreOffice. Source text
    // includes user-controlled profile fields (BurnerName, AllergyOtherText,
    // IntoleranceOtherText), so we prepend a literal apostrophe before applying
    // RFC 4180 quoting so the cell renders as text. Implementation lives in
    // <see cref="CsvCellQuoting"/> so the per-day matrix writer reuses the same
    // sanitization rules.
    private static string Quote(string s) => CsvCellQuoting.Quote(s);
}
