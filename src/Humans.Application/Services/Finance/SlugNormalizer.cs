using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Humans.Application.Services.Finance;

/// <summary>
/// Produces Holded-tag-safe slugs: lowercase, ASCII-only, dash-separated,
/// no leading/trailing dashes, no consecutive dashes. Idempotent.
/// </summary>
public static partial class SlugNormalizer
{
    [GeneratedRegex(@"[^a-z0-9]+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NonAlphaNumericRegex();

    [GeneratedRegex(@"-+", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DashCollapseRegex();

    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        // Decompose Unicode to separate base chars from combining diacritics, then strip diacritics.
        var decomposed = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var ch in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(ch);
            if (category != UnicodeCategory.NonSpacingMark)
                sb.Append(ch);
        }

        // Replace ñ→n explicitly: NFD doesn't fully strip the tilde for some inputs.
        var stripped = sb.ToString()
            .Replace('ñ', 'n').Replace('Ñ', 'N')
            .ToLowerInvariant();

        var dashed = NonAlphaNumericRegex().Replace(stripped, "-");
        var collapsed = DashCollapseRegex().Replace(dashed, "-");
        return collapsed.Trim('-');
    }
}
