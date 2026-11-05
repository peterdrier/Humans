using System.Globalization;
using System.Numerics;

namespace Humans.Base.Helpers;

public static class IbanValidator
{
    // SWIFT IBAN Registry, release 99 (June 2026). Currency is the country's ISO 4217 code (Bulgaria joined the
    // euro on 2026-01-01). Eea: EU plus Iceland, Liechtenstein and Norway, where EU rules cap euro transfer fees
    // at the domestic price.
    private static readonly Dictionary<string, (int Length, string Currency, bool Eea)> Countries = new(StringComparer.Ordinal)
    {
        ["AD"] = (24, "EUR", false),
        ["AE"] = (23, "AED", false),
        ["AL"] = (28, "ALL", false),
        ["AT"] = (20, "EUR", true),
        ["BE"] = (16, "EUR", true),
        ["BG"] = (22, "EUR", true),
        ["CH"] = (21, "CHF", false),
        ["CY"] = (28, "EUR", true),
        ["CZ"] = (24, "CZK", true),
        ["DE"] = (22, "EUR", true),
        ["DK"] = (18, "DKK", true),
        ["EE"] = (20, "EUR", true),
        ["ES"] = (24, "EUR", true),
        ["FI"] = (18, "EUR", true),
        ["FR"] = (27, "EUR", true),
        ["GB"] = (22, "GBP", false),
        ["GR"] = (27, "EUR", true),
        ["HR"] = (21, "EUR", true),
        ["HU"] = (28, "HUF", true),
        ["IE"] = (22, "EUR", true),
        ["IS"] = (26, "ISK", true),
        ["IT"] = (27, "EUR", true),
        ["LI"] = (21, "CHF", true),
        ["LT"] = (20, "EUR", true),
        ["LU"] = (20, "EUR", true),
        ["LV"] = (21, "EUR", true),
        ["MC"] = (27, "EUR", false),
        ["MD"] = (24, "MDL", false),
        ["ME"] = (22, "EUR", false),
        ["MK"] = (19, "MKD", false),
        ["MT"] = (31, "EUR", true),
        ["NL"] = (18, "EUR", true),
        ["NO"] = (15, "NOK", true),
        ["PL"] = (28, "PLN", true),
        ["PT"] = (25, "EUR", true),
        ["RO"] = (24, "RON", true),
        ["SE"] = (24, "SEK", true),
        ["SI"] = (19, "EUR", true),
        ["SK"] = (24, "EUR", true),
        ["SM"] = (27, "EUR", false),
        ["VA"] = (22, "EUR", false),
    };

    public static string Normalize(string? iban) =>
        (iban ?? "").Replace(" ", "").Replace(" ", "").ToUpperInvariant();

    /// <summary>
    /// The domestic ISO 4217 currency of the IBAN's country, read from its first two letters, so a masked IBAN works
    /// too. Not necessarily the account's currency. Null when the country is not in the registry.
    /// </summary>
    public static string? CurrencyOf(string? iban) => Lookup(iban)?.Currency;

    /// <summary>Whether the IBAN's country is in the EEA, read like <see cref="CurrencyOf"/>. Null when unknown.</summary>
    public static bool? IsEea(string? iban) => Lookup(iban)?.Eea;

    private static (int Length, string Currency, bool Eea)? Lookup(string? iban)
    {
        var v = Normalize(iban);
        return v.Length >= 2 && Countries.TryGetValue(v[..2], out var entry) ? entry : null;
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
