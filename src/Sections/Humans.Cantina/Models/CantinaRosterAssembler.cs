using Humans.Cantina.Domain;
using Humans.Cantina.Services.Dtos;

namespace Humans.Cantina.Models;

internal static class CantinaRosterAssembler
{
    /// <summary>
    /// First arrival, then anyone with allergies or intolerances, then dietary priority, then burner name by culture
    /// collation (Spanish names: <c>Ordinal</c> mis-sorts accents).
    /// </summary>
    public static IReadOnlyList<RosterPersonDto> SortForDisplay(IReadOnlyList<RosterPersonDto> people)
    {
        ArgumentNullException.ThrowIfNull(people);
        if (people.Count <= 1)
            return people;

        return people
            .OrderBy(p => p.ArrivesOn)
            .ThenByDescending(HasAnyAllergyOrIntolerance)
            .ThenBy(p => DietaryPriority(p.DietaryPreference))
            .ThenBy(p => p.BurnerName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public static bool HasAnyAllergyOrIntolerance(RosterPersonDto p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return p.Allergies.Count > 0
            || p.Intolerances.Count > 0
            || !string.IsNullOrWhiteSpace(p.AllergyOtherText)
            || !string.IsNullOrWhiteSpace(p.IntoleranceOtherText);
    }

    /// <summary>Canonical preferences first, then unknown legacy values, then unanswered last.</summary>
    public static int DietaryPriority(string? dietary)
    {
        if (string.IsNullOrEmpty(dietary)) return int.MaxValue;
        for (var i = 0; i < DietaryOptions.DietaryPreferences.Count; i++)
        {
            if (string.Equals(DietaryOptions.DietaryPreferences[i], dietary, StringComparison.Ordinal))
                return i;
        }
        return int.MaxValue - 1;
    }

    public static WeeklyRosterDto WithSortedPeople(WeeklyRosterDto roster)
    {
        ArgumentNullException.ThrowIfNull(roster);
        return roster with { People = SortForDisplay(roster.People) };
    }

    /// <summary>
    /// Alphabetical by burner name, deliberately not the weekly multi-key sort: the daily matrix is a look-up surface
    /// ("find this person's row").
    /// </summary>
    public static DailyMatrixDto WithSortedPeople(DailyMatrixDto matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        if (matrix.People.Count <= 1)
            return matrix;

        var sorted = matrix.People
            .OrderBy(p => p.BurnerName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
        return matrix with { People = sorted };
    }
}
