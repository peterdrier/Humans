using System.Globalization;

namespace Humans.Application.Extensions;

/// <summary>
/// The one sanctioned home for money formatting. Renders euro amounts with the
/// symbol trailing the number ("1.234,56 €"), so figures right-align and scan
/// cleanly in tables. Grouping/decimal separators follow the request culture.
///
/// Never write a leading currency symbol, a raw <c>&amp;euro;</c>/<c>€</c>, or a
/// <c>"$"</c> prefix in a view — route every amount through here.
/// </summary>
public static class CurrencyFormattingExtensions
{
    /// <summary>Euro amount, symbol trailing (e.g. "1.234,56 €"). <paramref name="decimals"/> defaults to 2; pass 0 for whole-euro displays.</summary>
    public static string ToEuro(this decimal value, int decimals = 2) =>
        value.ToString("N" + decimals.ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture) + " €";

    /// <summary>Euro amount for a nullable value; null renders as the empty string.</summary>
    public static string ToEuro(this decimal? value, int decimals = 2) =>
        value.HasValue ? value.Value.ToEuro(decimals) : string.Empty;
}
