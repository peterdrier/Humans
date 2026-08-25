using System.Globalization;
using System.Text;

namespace Humans.Finance.Services;

/// <summary>
/// Folds a name or a remittance line into the restricted SEPA character subset Sabadell accepts:
/// <c>a-z A-Z 0-9 / - ? : ( ) . , ' +</c> and space. Accents decompose (Ñ→N, Ç→C, Á→A); the few
/// letters that do not decompose are mapped by hand; anything still outside the set becomes a
/// space. XML-reserved characters need no handling here — the writer escapes them.
/// </summary>
internal static class SepaText
{
    private const string Allowed = "/-?:().,'+ ";

    private static readonly Dictionary<char, string> NonDecomposing = new()
    {
        ['ß'] = "ss",
        ['Ø'] = "O",
        ['ø'] = "o",
        ['Æ'] = "AE",
        ['æ'] = "ae",
        ['Œ'] = "OE",
        ['œ'] = "oe",
        ['Đ'] = "D",
        ['đ'] = "d",
        ['Ð'] = "D",
        ['ð'] = "d",
        ['Þ'] = "TH",
        ['þ'] = "th",
        ['Ł'] = "L",
        ['ł'] = "l",
    };

    public static string Normalize(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";

        var expanded = new StringBuilder(value.Length);
        foreach (var c in value)
            expanded.Append(NonDecomposing.TryGetValue(c, out var replacement) ? replacement : c.ToString());

        var decomposed = expanded.ToString().Normalize(NormalizationForm.FormD);

        var result = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;

            var keep = (c is >= 'a' and <= 'z') || (c is >= 'A' and <= 'Z') || (c is >= '0' and <= '9')
                       || Allowed.Contains(c, StringComparison.Ordinal);
            var next = keep ? c : ' ';

            // Collapse runs, and never open with a space.
            if (next == ' ' && (result.Length == 0 || result[^1] == ' ')) continue;
            result.Append(next);
        }

        var trimmed = result.ToString().TrimEnd();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength].TrimEnd();
    }
}
