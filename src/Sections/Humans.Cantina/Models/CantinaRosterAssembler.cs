using Humans.Cantina.Services.Dtos;
using Humans.Users.Contracts;

namespace Humans.Cantina.Models;

/// <summary>
/// Presentation helper for the Cantina roster payloads. Sort-for-display lives
/// here, not in <c>CantinaRosterService</c>: display ordering is a presentation
/// concern (see <c>memory/architecture/display-sort-in-controllers.md</c>). The
/// Application service returns <see cref="WeeklyRosterDto.People"/> in
/// unspecified order; the controller pipes the DTO through
/// <see cref="WithSortedPeople(WeeklyRosterDto)"/> before handing it to the CSV
/// writer, and the daily payload through its overload before the matrix view and
/// that view's CSV.
/// </summary>
internal static class CantinaRosterAssembler
{
    /// <summary>
    /// Coordinator-friendly sort for the weekly CSV's per-person rows:
    /// <list type="number">
    ///   <item>First arrival date asc (<see cref="RosterPersonDto.ArrivesOn"/>)
    ///         — earliest-on-site humans surface to the top.</item>
    ///   <item>Has any allergies or intolerances desc — higher-attention
    ///         dietary needs surface first within an arrival day.</item>
    ///   <item>Dietary preference in canonical order (Omnivore, Vegetarian,
    ///         Vegan, Pescatarian), unknown/legacy values next, unanswered last.</item>
    ///   <item><see cref="RosterPersonDto.BurnerName"/> cultural-collation asc
    ///         (Spanish event, Spanish names with ñ/á/í — <c>Ordinal</c> would
    ///         mis-sort accented characters).</item>
    /// </list>
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

    /// <summary>
    /// True when the human has any allergy or intolerance signal at all
    /// (chip or free-text). Used to surface higher-attention humans to the
    /// top of the People list.
    /// </summary>
    public static bool HasAnyAllergyOrIntolerance(RosterPersonDto p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return p.Allergies.Count > 0
            || p.Intolerances.Count > 0
            || !string.IsNullOrWhiteSpace(p.AllergyOtherText)
            || !string.IsNullOrWhiteSpace(p.IntoleranceOtherText);
    }

    /// <summary>
    /// Sort key for the dietary tiebreaker. Canonical order
    /// (Omnivore..Pescatarian) first, then unknown/legacy values, then
    /// null/empty Unanswered last so coordinators see the answered people
    /// grouped together at the top.
    /// </summary>
    public static int DietaryPriority(string? dietary)
    {
        if (string.IsNullOrEmpty(dietary)) return int.MaxValue;
        for (var i = 0; i < DietaryOptions.DietaryPreferences.Count; i++)
        {
            if (string.Equals(DietaryOptions.DietaryPreferences[i], dietary, StringComparison.Ordinal))
                return i;
        }
        // Unknown/legacy value — sort right before the truly-unanswered bucket.
        return int.MaxValue - 1;
    }

    /// <summary>
    /// Returns a copy of <paramref name="roster"/> with its
    /// <see cref="WeeklyRosterDto.People"/> replaced by
    /// <see cref="SortForDisplay"/>'s output. Its one caller is the weekly CSV
    /// action, which hands the sorted DTO straight to the CSV writer; the weekly
    /// page renders none of <see cref="WeeklyRosterDto.People"/> and does not sort.
    /// </summary>
    public static WeeklyRosterDto WithSortedPeople(WeeklyRosterDto roster)
    {
        ArgumentNullException.ThrowIfNull(roster);
        return roster with { People = SortForDisplay(roster.People) };
    }

    /// <summary>
    /// Returns a copy of <paramref name="matrix"/> with its
    /// <see cref="DailyMatrixDto.People"/> replaced by an alphabetical sort
    /// of <see cref="DailyPersonRowDto.BurnerName"/> (cultural-collation,
    /// case-insensitive — Spanish event, Spanish names with ñ/á/í).
    ///
    /// <para>
    /// Deliberately NOT the same multi-key sort used for the weekly CSV
    /// (<see cref="SortForDisplay(IReadOnlyList{RosterPersonDto})"/>): the
    /// daily matrix is a coordinator look-up surface (matrix-scan by column,
    /// then "find this specific person on the row"), so alphabetical is
    /// more useful than arrival/allergy/dietary priority.
    /// </para>
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
