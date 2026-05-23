using System.Globalization;
using System.Text;
using Humans.Application.Services.Cantina.Dtos;
using NodaTime.Text;

namespace Humans.Web.Cantina;

/// <summary>
/// Renders a <see cref="WeeklyRosterDto"/> as a UTF-8 CSV byte payload.
/// Used by <c>CantinaController.Csv</c> (feature #36 —
/// docs/features/cantina/daily-roster.md). Distinct from
/// <c>Humans.Web.Extensions.CsvExtensions</c>: that helper quotes every
/// field unconditionally; the cantina export follows RFC 4180 conditional
/// quoting so the output stays readable when opened in a spreadsheet
/// without parser warnings. Multi-select fields (allergies, intolerances)
/// are joined with <c>", "</c> into a single cell. The <c>DaysOnSite</c>
/// column lists short calendar labels (e.g. "Mon 27 May") comma-and-space-
/// separated.
/// </summary>
public static class CantinaRosterCsvWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    // NodaTime pattern: short weekday + day of month + short month, invariant.
    // Example: "Mon 27 May".
    private static readonly LocalDatePattern DayOnSitePattern =
        LocalDatePattern.CreateWithInvariantCulture("ddd d MMM");

    public static byte[] Write(WeeklyRosterDto roster)
    {
        ArgumentNullException.ThrowIfNull(roster);

        using var ms = new MemoryStream();
        ms.Write(Utf8Bom, 0, Utf8Bom.Length);
        using (var sw = new StreamWriter(ms, Utf8NoBom, leaveOpen: true) { NewLine = "\r\n" })
        {
            sw.WriteLine("Name,DaysOnSite,Dietary,Allergies,AllergyOther,Intolerances,IntoleranceOther");
            foreach (var p in roster.People)
            {
                sw.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0},{1},{2},{3},{4},{5},{6}",
                    Quote(p.BurnerName),
                    Quote(FormatDaysOnSite(p.DaysOnSite)),
                    Quote(p.DietaryPreference ?? string.Empty),
                    Quote(string.Join(", ", p.Allergies)),
                    Quote(p.AllergyOtherText ?? string.Empty),
                    Quote(string.Join(", ", p.Intolerances)),
                    Quote(p.IntoleranceOtherText ?? string.Empty)));
            }
        }

        return ms.ToArray();
    }

    private static string FormatDaysOnSite(IReadOnlyList<NodaTime.LocalDate> days)
    {
        if (days.Count == 0)
            return string.Empty;
        var parts = new string[days.Count];
        for (var i = 0; i < days.Count; i++)
            parts[i] = DayOnSitePattern.Format(days[i]);
        return string.Join(", ", parts);
    }

    private static string Quote(string s)
    {
        if (s.Length == 0)
            return string.Empty;

        var needsQuoting = s.IndexOfAny(['"', ',', '\n', '\r']) >= 0;
        if (!needsQuoting)
            return s;

        return "\"" + s.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }
}
