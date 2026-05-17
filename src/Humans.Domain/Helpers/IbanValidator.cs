using System.Globalization;
using System.Numerics;

namespace Humans.Domain.Helpers;

public static class IbanValidator
{
    private static readonly Dictionary<string, int> Lengths = new(StringComparer.Ordinal)
    {
        ["AD"] = 24,
        ["AE"] = 23,
        ["AT"] = 20,
        ["BE"] = 16,
        ["BG"] = 22,
        ["CH"] = 21,
        ["CY"] = 28,
        ["CZ"] = 24,
        ["DE"] = 22,
        ["DK"] = 18,
        ["EE"] = 20,
        ["ES"] = 24,
        ["FI"] = 18,
        ["FR"] = 27,
        ["GB"] = 22,
        ["GR"] = 27,
        ["HR"] = 21,
        ["HU"] = 28,
        ["IE"] = 22,
        ["IS"] = 26,
        ["IT"] = 27,
        ["LI"] = 21,
        ["LT"] = 20,
        ["LU"] = 20,
        ["LV"] = 21,
        ["MC"] = 27,
        ["MT"] = 31,
        ["NL"] = 18,
        ["NO"] = 15,
        ["PL"] = 28,
        ["PT"] = 25,
        ["RO"] = 24,
        ["SE"] = 24,
        ["SI"] = 19,
        ["SK"] = 24,
        ["SM"] = 27,
    };

    public static string Normalize(string? iban) =>
        (iban ?? "").Replace(" ", "").Replace(" ", "").ToUpperInvariant();

    public static bool IsValid(string? iban)
    {
        var v = Normalize(iban);
        if (v.Length < 4) return false;
        var country = v[..2];
        if (!Lengths.TryGetValue(country, out var expectedLen)) return false;
        if (v.Length != expectedLen) return false;
        if (!v.All(char.IsLetterOrDigit)) return false;

        var rearranged = v[4..] + v[..4];
        var sb = new System.Text.StringBuilder(rearranged.Length * 2);
        foreach (var c in rearranged)
        {
            if (char.IsDigit(c)) sb.Append(c);
            else sb.Append((c - 'A' + 10).ToString(CultureInfo.InvariantCulture));
        }
        var big = BigInteger.Parse(sb.ToString(), CultureInfo.InvariantCulture);
        return big % 97 == 1;
    }
}
