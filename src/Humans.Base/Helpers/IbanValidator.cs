using System.Globalization;
using System.Numerics;

namespace Humans.Base.Helpers;

public static class IbanValidator
{
    // SWIFT IBAN Registry, release 99 (June 2026). Currency is the country's ISO 4217 code: Bulgaria joined
    // the euro on 2026-01-01.
    private static readonly Dictionary<string, (int Length, string Currency)> Countries = new(StringComparer.Ordinal)
    {
        ["AD"] = (24, "EUR"),
        ["AE"] = (23, "AED"),
        ["AL"] = (28, "ALL"),
        ["AT"] = (20, "EUR"),
        ["BE"] = (16, "EUR"),
        ["BG"] = (22, "EUR"),
        ["CH"] = (21, "CHF"),
        ["CY"] = (28, "EUR"),
        ["CZ"] = (24, "CZK"),
        ["DE"] = (22, "EUR"),
        ["DK"] = (18, "DKK"),
        ["EE"] = (20, "EUR"),
        ["ES"] = (24, "EUR"),
        ["FI"] = (18, "EUR"),
        ["FR"] = (27, "EUR"),
        ["GB"] = (22, "GBP"),
        ["GR"] = (27, "EUR"),
        ["HR"] = (21, "EUR"),
        ["HU"] = (28, "HUF"),
        ["IE"] = (22, "EUR"),
        ["IS"] = (26, "ISK"),
        ["IT"] = (27, "EUR"),
        ["LI"] = (21, "CHF"),
        ["LT"] = (20, "EUR"),
        ["LU"] = (20, "EUR"),
        ["LV"] = (21, "EUR"),
        ["MC"] = (27, "EUR"),
        ["MD"] = (24, "MDL"),
        ["ME"] = (22, "EUR"),
        ["MK"] = (19, "MKD"),
        ["MT"] = (31, "EUR"),
        ["NL"] = (18, "EUR"),
        ["NO"] = (15, "NOK"),
        ["PL"] = (28, "PLN"),
        ["PT"] = (25, "EUR"),
        ["RO"] = (24, "RON"),
        ["SE"] = (24, "SEK"),
        ["SI"] = (19, "EUR"),
        ["SK"] = (24, "EUR"),
        ["SM"] = (27, "EUR"),
        ["VA"] = (22, "EUR"),
    };

    public static string Normalize(string? iban) =>
        (iban ?? "").Replace(" ", "").Replace(" ", "").ToUpperInvariant();

    /// <summary>
    /// The ISO 4217 currency of the IBAN's country, read from its first two letters, so a masked IBAN works too.
    /// Null when the country is not in the registry.
    /// </summary>
    public static string? CurrencyOf(string? iban)
    {
        var v = Normalize(iban);
        return v.Length >= 2 && Countries.TryGetValue(v[..2], out var entry) ? entry.Currency : null;
    }

    public static bool IsValid(string? iban)
    {
        var v = Normalize(iban);
        if (v.Length < 4) return false;
        var country = v[..2];
        if (!Countries.TryGetValue(country, out var entry)) return false;
        var expectedLen = entry.Length;
        if (v.Length != expectedLen) return false;
        if (!char.IsAsciiDigit(v[2]) || !char.IsAsciiDigit(v[3])) return false;
        if (!v.All(char.IsAsciiLetterOrDigit)) return false;

        var rearranged = v[4..] + v[..4];
        var sb = new System.Text.StringBuilder(rearranged.Length * 2);
        foreach (var c in rearranged)
        {
            if (char.IsAsciiDigit(c)) sb.Append(c);
            else sb.Append((c - 'A' + 10).ToString(CultureInfo.InvariantCulture));
        }
        var big = BigInteger.Parse(sb.ToString(), CultureInfo.InvariantCulture);
        return big % 97 == 1;
    }
}
