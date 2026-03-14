using System.Text.RegularExpressions;

namespace Humans.Infrastructure.Helpers;

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

    [GeneratedRegex(@"[^a-z0-9\-]", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NonAlphanumericRegex();

    [GeneratedRegex(@"-{2,}", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex MultipleHyphensRegex();
}
