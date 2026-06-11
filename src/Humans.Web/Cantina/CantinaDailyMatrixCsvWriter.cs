using System.Globalization;
using Humans.Application.Csv;
using Humans.Application.Extensions;
using Humans.Application.Services.Cantina.Dtos;
using Humans.Domain.Constants;
using NodaTime;

namespace Humans.Web.Cantina;

/// <summary>
/// Renders a <see cref="DailyMatrixDto"/> as a UTF-8 CSV byte payload — the
/// per-day drill-down companion to <see cref="CantinaRosterCsvWriter"/>
/// (feature #36 — docs/features/cantina/daily-roster.md). Quoting and OWASP
/// injection escaping come from the shared <see cref="HumansCsv"/> conventions.
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
    public static byte[] Write(DailyMatrixDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        return HumansCsv.WriteBytes(csv =>
        {
            // ---- Header section (3 lines + blank separator) -----------------
            csv.WriteRow(string.Format(CultureInfo.InvariantCulture, "Cantina — {0}", FormatLongDate(dto.CalendarDate)));
            csv.WriteRow(string.Format(CultureInfo.InvariantCulture, "Total on site: {0}", dto.TotalOnSite));
            csv.WriteRow(string.Format(CultureInfo.InvariantCulture, "Unanswered: {0}", dto.UnansweredCount));
            csv.NextRecord();

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
            csv.WriteRow([.. cols]);

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
                csv.WriteRow([.. row]);
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
                csv.WriteRow([.. totals]);
            }
        });
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
        d.HasValue ? d.Value.ToInvariantDate() : "(no active event)";
}
