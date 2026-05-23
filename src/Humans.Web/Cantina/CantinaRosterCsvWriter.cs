using System.Globalization;
using System.Text;
using Humans.Application.Services.Cantina.Dtos;

namespace Humans.Web.Cantina;

/// <summary>
/// Renders a <see cref="DailyRosterDto"/> as a UTF-8 CSV byte payload.
/// Used by <c>CantinaController.Csv</c> (feature #36 —
/// docs/features/cantina/daily-roster.md). Distinct from
/// <c>Humans.Web.Extensions.CsvExtensions</c>: that helper quotes every
/// field unconditionally; the cantina export follows RFC 4180 conditional
/// quoting so the output stays readable when opened in a spreadsheet
/// without parser warnings. Multi-select fields (allergies, intolerances)
/// are joined with <c>", "</c> into a single cell.
/// </summary>
public static class CantinaRosterCsvWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    public static byte[] Write(DailyRosterDto roster)
    {
        ArgumentNullException.ThrowIfNull(roster);

        using var ms = new MemoryStream();
        ms.Write(Utf8Bom, 0, Utf8Bom.Length);
        using (var sw = new StreamWriter(ms, Utf8NoBom, leaveOpen: true) { NewLine = "\r\n" })
        {
            sw.WriteLine("Name,Dietary,Allergies,AllergyOther,Intolerances,IntoleranceOther");
            foreach (var p in roster.People)
            {
                sw.WriteLine(string.Format(
                    CultureInfo.InvariantCulture,
                    "{0},{1},{2},{3},{4},{5}",
                    Quote(p.BurnerName),
                    Quote(p.DietaryPreference ?? string.Empty),
                    Quote(string.Join(", ", p.Allergies)),
                    Quote(p.AllergyOtherText ?? string.Empty),
                    Quote(string.Join(", ", p.Intolerances)),
                    Quote(p.IntoleranceOtherText ?? string.Empty)));
            }
        }

        return ms.ToArray();
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
