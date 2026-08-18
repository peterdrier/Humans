using System.Text.RegularExpressions;

namespace Humans.Application.Helpers;

public static partial class SlugHelper
{
    private static readonly HashSet<string> ReservedCampSlugs = new(StringComparer.OrdinalIgnoreCase)
    {
        "register", "admin"
    };

    public static string GenerateSlug(string name)
    {
        var slug = name.ToLowerInvariant();
        slug = NonAlphanumericRegex().Replace(slug, "-");
        slug = MultipleHyphensRegex().Replace(slug, "-");
        slug = slug.Trim('-');
        return slug;
    }

    public static bool IsReservedCampSlug(string slug)
    {
        return ReservedCampSlugs.Contains(slug);
    }

    /// <summary>
    /// Returns true when <paramref name="slug"/> is a well-formed kebab-case slug:
    /// lowercase ASCII letters / digits / hyphens, starts and ends with an
    /// alphanumeric, no consecutive hyphens, length 1..maxLength.
    /// </summary>
    public static bool IsValidKebabSlug(string? slug, int maxLength = 60)
    {
        if (string.IsNullOrEmpty(slug) || slug.Length > maxLength) return false;
        return KebabSlugRegex().IsMatch(slug);
    }

    [GeneratedRegex(@"[^a-z0-9\-]", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NonAlphanumericRegex();

    [GeneratedRegex(@"-{2,}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MultipleHyphensRegex();

    [GeneratedRegex(@"^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.ExplicitCapture, matchTimeoutMilliseconds: 1000)]
    private static partial Regex KebabSlugRegex();
}
