namespace Humans.Domain.Helpers;

/// <summary>
/// Centralized IBAN masking. All log/audit/error output that
/// references an IBAN MUST go through Mask. Raw IBANs only appear
/// in pain.001 SEPA XML and in the Holded API request body.
/// </summary>
public static class IbanFormatter
{
    public static string Mask(string? iban)
    {
        if (string.IsNullOrEmpty(iban)) return "";
        var compact = iban.Replace(" ", "").Replace(" ", "");
        if (compact.Length <= 7) return "****";
        return $"{compact[..4]}****{compact[^3..]}";
    }
}
