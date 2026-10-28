using Humans.Cantina.Domain;
using System.Globalization;
using Humans.Base.Csv;
using Humans.Base.Extensions;
using Humans.Cantina.Services.Dtos;
using NodaTime;

namespace Humans.Cantina.Models;

/// <summary>Column order mirrors the on-screen matrix in <c>Views/Cantina/Day.cshtml</c>.</summary>
internal static class CantinaDailyMatrixCsvWriter
{
    public static byte[] Write(DailyMatrixDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        return HumansCsv.WriteBytes(csv =>
        {
            csv.WriteRow(string.Format(CultureInfo.InvariantCulture, "Cantina — {0}", FormatIsoDate(dto.CalendarDate)));
            csv.WriteRow(string.Format(CultureInfo.InvariantCulture, "Total on site: {0}", dto.TotalOnSite));
            csv.WriteRow(string.Format(CultureInfo.InvariantCulture, "Unanswered: {0}", dto.UnansweredCount));
            csv.NextRecord();

            var diet = DietaryOptions.DietaryPreferences;
            // "Other" gets its own flag and free-text columns instead.
            var allergyChips = DietaryOptions.AllergyOptions.Where(a => !string.Equals(a, DietaryOptions.OtherOption, StringComparison.Ordinal)).ToList();
            var intoleranceChips = DietaryOptions.IntoleranceOptions.Where(i => !string.Equals(i, DietaryOptions.OtherOption, StringComparison.Ordinal)).ToList();

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

    private static string CountAsString(int n) => n.ToString(CultureInfo.InvariantCulture);

    // Invariant ISO: a CSV cell is machine-read as often as it is eyeballed.
    private static string FormatIsoDate(LocalDate? d) =>
        d.HasValue ? d.Value.ToInvariantDate() : "(no active event)";
}
