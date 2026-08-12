using System.Diagnostics.CodeAnalysis;

namespace Humans.UI.Extensions;

/// <summary>
/// Two pure-string search predicates. In <c>Humans.UI</c> rather than Shell because
/// <c>Humans.Teams</c>' admin controller filters its member list with them and a section
/// cannot reference <c>Humans.Web</c>; they name no section vocabulary, which is the test
/// (design §15 step 6, Gate's <c>OrderByRelevance</c> call).
/// </summary>
public static class StringSearchExtensions
{
    public static bool HasSearchTerm([NotNullWhen(true)] this string? value, int minLength = 2)
    {
        return !string.IsNullOrWhiteSpace(value) && value.Trim().Length >= minLength;
    }

    public static bool ContainsOrdinalIgnoreCase(this string? source, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return source?.Contains(value, StringComparison.OrdinalIgnoreCase) == true;
    }
}
