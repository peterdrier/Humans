using System.Diagnostics.CodeAnalysis;

namespace Humans.Base.Extensions;

/// <summary>
/// Pure-string search predicates and the shared name-match rubric. In <c>Humans.Base</c>
/// rather than Shell because <c>Humans.Teams</c>' admin controller filters its member list
/// with them and a section cannot reference <c>Humans.Web</c>; they name no section
/// vocabulary, which is the test (design §15 step 6, Gate's <c>OrderByRelevance</c> call).
/// </summary>
public static class StringSearchExtensions
{
    /// <summary>A query equal to the whole name.</summary>
    public const int ExactNameScore = 100;

    /// <summary>A query the name starts with.</summary>
    public const int PrefixNameScore = 80;

    /// <summary>A query the name contains anywhere.</summary>
    public const int ContainsNameScore = 60;

    public static bool HasSearchTerm([NotNullWhen(true)] this string? value, int minLength = 2)
    {
        return !string.IsNullOrWhiteSpace(value) && value.Trim().Length >= minLength;
    }

    public static bool ContainsOrdinalIgnoreCase(this string? source, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return source?.Contains(value, StringComparison.OrdinalIgnoreCase) == true;
    }

    /// <summary>
    /// Name-match strength for global search: exact &gt; prefix &gt; contains, 0 for no match.
    /// Each searchable section scores its own hits with this so the buckets stay comparable
    /// (nobodies-collective/Humans#1062).
    /// </summary>
    public static int NameMatchScore(this string? name, string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (string.IsNullOrEmpty(name)) return 0;
        if (string.Equals(name, query, StringComparison.OrdinalIgnoreCase)) return ExactNameScore;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return PrefixNameScore;
        if (name.Contains(query, StringComparison.OrdinalIgnoreCase)) return ContainsNameScore;
        return 0;
    }
}
