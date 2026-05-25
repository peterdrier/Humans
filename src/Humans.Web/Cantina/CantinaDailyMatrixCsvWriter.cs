using System.Globalization;
using System.Text;
using Humans.Application.Services.Cantina.Dtos;
using Humans.Domain.Constants;
using NodaTime;
using NodaTime.Text;

namespace Humans.Web.Cantina;

/// <summary>
/// Renders a <see cref="DailyMatrixDto"/> as a UTF-8 CSV byte payload — the
/// per-day drill-down companion to <see cref="CantinaRosterCsvWriter"/>
/// (feature #36 — docs/features/cantina/daily-roster.md). Same RFC 4180 +
/// OWASP formula-escape rules via <see cref="CsvCellQuoting"/>.
///
/// <para>
/// Output layout (top to bottom):
/// <list type="number">
///   <item>3-line header: title (with long-format date), total on site,
///         unanswered count.</item>
///   <item>Blank separator row.</item>
///   <item>Column-header row: <c>Burner</c>, dietary columns, allergy
///         chip columns + "Other allergy" flag + "Other allergy text",
///         intolerance chip columns + "Other intolerance" flag +
///         "Other intolerance text".</item>
///   <item>One row per person — <c>x</c> for ticked, empty cell otherwise.</item>
///   <item>"TOTALS" row — column-by-column counts. Free-text columns get
///         <c>—</c> since they can't be summed.</item>
/// </list>
/// Column order mirrors the on-screen matrix in <c>Views/Cantina/Day.cshtml</c>
/// so coordinators can read the export the same way as the screen.
/// </para>
/// </summary>
public static class CantinaDailyMatrixCsvWriter
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    // Long human-readable date in invariant culture so the export reads the
    // same regardless of the requester's browser locale. Example:
    // "Tuesday 7 July 2026".
    private static readonly LocalDatePattern LongDayPattern =
        LocalDatePattern.CreateWithInvariantCulture("dddd d MMMM yyyy");

    public static byte[] Write(DailyMatrixDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        using var ms = new MemoryStream();
        ms.Write(Utf8Bom, 0, Utf8Bom.Length);
        using (var sw = new StreamWriter(ms, Utf8NoBom, leaveOpen: true) { NewLine = "\r\n" })
        {
            // ---- Header section (3 lines + blank separator) -----------------
            sw.WriteLine(CsvCellQuoting.Quote(
                string.Format(CultureInfo.InvariantCulture, "Cantina — {0}", FormatLongDate(dto.CalendarDate))));
            sw.WriteLine(CsvCellQuoting.Quote(
                string.Format(CultureInfo.InvariantCulture, "Total on site: {0}", dto.TotalOnSite)));
            sw.WriteLine(CsvCellQuoting.Quote(
                string.Format(CultureInfo.InvariantCulture, "Unanswered: {0}", dto.UnansweredCount)));
            sw.WriteLine();

            // ---- Column headers --------------------------------------------
            var diet = DietaryOptions.DietaryPreferences;
            // Filter out the "Other" sentinel from chip columns — it gets its
            // own dedicated flag + free-text column to keep the layout regular.
            var allergyChips = WithoutOther(DietaryOptions.AllergyOptions);
            var intoleranceChips = WithoutOther(DietaryOptions.IntoleranceOptions);

            var cols = new List<string>(capacity: 1 + diet.Count + allergyChips.Count + 2 + intoleranceChips.Count + 2)
            {
                "Burner"
            };
            cols.AddRange(diet);
            cols.AddRange(allergyChips);
            cols.Add("Other allergy");
            cols.Add("Other allergy text");
            cols.AddRange(intoleranceChips);
            cols.Add("Other intolerance");
            cols.Add("Other intolerance text");
            sw.WriteLine(string.Join(",", cols.Select(CsvCellQuoting.Quote)));

            // ---- People rows -----------------------------------------------
            foreach (var p in dto.People)
            {
                var row = new List<string>(cols.Count) { p.BurnerName };
                foreach (var d in diet)
                    row.Add(string.Equals(p.DietaryPreference, d, StringComparison.Ordinal) ? "x" : string.Empty);
                foreach (var a in allergyChips)
                    row.Add(p.Allergies.Contains(a) ? "x" : string.Empty);
                row.Add(p.Allergies.Contains(DietaryOptions.OtherOption) ? "x" : string.Empty);
                row.Add(p.AllergyOtherText ?? string.Empty);
                foreach (var i in intoleranceChips)
                    row.Add(p.Intolerances.Contains(i) ? "x" : string.Empty);
                row.Add(p.Intolerances.Contains(DietaryOptions.OtherOption) ? "x" : string.Empty);
                row.Add(p.IntoleranceOtherText ?? string.Empty);
                sw.WriteLine(string.Join(",", row.Select(CsvCellQuoting.Quote)));
            }

            // ---- TOTALS row (only when there's at least one person) --------
            if (dto.People.Count > 0)
            {
                var totals = new List<string>(cols.Count) { "TOTALS" };
                foreach (var d in diet)
                    totals.Add(CountAsString(dto.People.Count(p => string.Equals(p.DietaryPreference, d, StringComparison.Ordinal))));
                foreach (var a in allergyChips)
                    totals.Add(CountAsString(dto.People.Count(p => p.Allergies.Contains(a))));
                totals.Add(CountAsString(dto.People.Count(p => p.Allergies.Contains(DietaryOptions.OtherOption))));
                totals.Add("—"); // free text can't be summed
                foreach (var i in intoleranceChips)
                    totals.Add(CountAsString(dto.People.Count(p => p.Intolerances.Contains(i))));
                totals.Add(CountAsString(dto.People.Count(p => p.Intolerances.Contains(DietaryOptions.OtherOption))));
                totals.Add("—");
                sw.WriteLine(string.Join(",", totals.Select(CsvCellQuoting.Quote)));
            }
        }

        return ms.ToArray();
    }

    private static List<string> WithoutOther(IReadOnlyList<string> options)
    {
        var result = new List<string>(options.Count);
        foreach (var o in options)
        {
            if (!string.Equals(o, DietaryOptions.OtherOption, StringComparison.Ordinal))
                result.Add(o);
        }
        return result;
    }

    private static string CountAsString(int n) => n.ToString(CultureInfo.InvariantCulture);

    private static string FormatLongDate(LocalDate? d) =>
        d.HasValue ? LongDayPattern.Format(d.Value) : "(no active event)";
}
