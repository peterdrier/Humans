using System.Text.RegularExpressions;

namespace Humans.Base.Helpers;

/// <summary>
/// Centralized IBAN masking. All log/audit/error output that
/// references an IBAN MUST go through Mask. Raw IBANs only appear
/// in pain.001 SEPA XML and in the Holded API request body.
/// </summary>
public static partial class IbanFormatter
{
    public static string Mask(string? iban)
    {
        if (string.IsNullOrEmpty(iban)) return "";
        var compact = iban.Replace(" ", "").Replace(" ", "");
        if (compact.Length <= 7) return "****";
        return $"{compact[..4]}****{compact[^3..]}";
    }

    /// <summary>
    /// Masks every IBAN-shaped token inside free text. For text we did not compose ourselves — a
    /// vendor's HTTP error body, say — we do not know which field holds the IBAN, only that one we
    /// just sent may be echoed back. Anything that reaches a log, an audit entry or the UI goes
    /// through here first.
    /// </summary>
    public static string MaskAllIn(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        return IbanShaped().Replace(text, m => Mask(m.Value));
    }

    // ISO 13616: 2 letters, 2 check digits, then 11–30 alphanumerics. Bounded on both sides so a
    // longer alphanumeric run — a Holded doc id, a hash — is not mistaken for an account number.
    [GeneratedRegex(@"\b[A-Z]{2}[0-9]{2}[A-Z0-9]{11,30}\b",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex IbanShaped();
}
