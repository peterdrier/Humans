namespace Humans.Web.Extensions;

public static class CultureCatalog
{
    public const string DefaultCultureCode = "en";
    public const string CanonicalLegalCultureCode = "es";

    public static IReadOnlyList<string> SupportedCultureCodes { get; } =
        ["en", "es", "de", "it", "fr"];
}

public static class CultureCodeExtensions
{
    private static readonly IReadOnlyDictionary<string, string> DisplayNames =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [CultureCatalog.DefaultCultureCode] = "English",
            [CultureCatalog.CanonicalLegalCultureCode] = "Castellano",
            ["de"] = "Deutsch",
            ["fr"] = "Fran\u00e7ais",
            ["it"] = "Italiano",
            ["pt"] = "Portugu\u00eas",
            ["ca"] = "Catal\u00e0"
        };

    public static bool IsSupportedCultureCode(this string? cultureCode)
    {
        return !string.IsNullOrWhiteSpace(cultureCode) &&
               CultureCatalog.SupportedCultureCodes.Contains(cultureCode, StringComparer.Ordinal);
    }

    public static string ToDisplayLanguageName(this string? cultureCode)
    {
        if (string.IsNullOrWhiteSpace(cultureCode))
        {
            return DisplayNames[CultureCatalog.DefaultCultureCode];
        }

        return DisplayNames.TryGetValue(cultureCode, out var name)
            ? name
            : cultureCode.ToUpperInvariant();
    }

    public static string ToLegalLanguageLabel(this string? cultureCode)
    {
        var name = cultureCode.ToDisplayLanguageName();
        return string.Equals(cultureCode, CultureCatalog.CanonicalLegalCultureCode, StringComparison.Ordinal)
            ? $"{name} (Legal)"
            : name;
    }

    public static IOrderedEnumerable<KeyValuePair<string, T>> OrderByDisplayLanguage<T>(
        this IEnumerable<KeyValuePair<string, T>> source,
        bool canonicalFirst = false)
    {
        ArgumentNullException.ThrowIfNull(source);

        return canonicalFirst
            ? source.OrderByDescending(kv => string.Equals(kv.Key, CultureCatalog.CanonicalLegalCultureCode, StringComparison.Ordinal))
                .ThenBy(kv => kv.Key.ToDisplayLanguageName(), StringComparer.Ordinal)
            : source.OrderBy(kv => kv.Key.ToDisplayLanguageName(), StringComparer.Ordinal);
    }

    public static string GetDefaultDocumentLanguage<T>(this IReadOnlyDictionary<string, T> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        var userLanguage = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        return documents.ContainsKey(userLanguage)
            ? userLanguage
            : CultureCatalog.CanonicalLegalCultureCode;
    }
}
