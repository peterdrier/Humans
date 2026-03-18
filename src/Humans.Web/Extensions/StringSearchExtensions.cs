using System.Diagnostics.CodeAnalysis;

namespace Humans.Web.Extensions;

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
