using System.Text;
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
    /// Masks every IBAN inside free text. For text we did not compose ourselves — a vendor's HTTP
    /// error body, a member's note — we know neither which field holds the IBAN nor how it was
    /// typed. Two things count: an unspaced upper-case IBAN-shaped token (a vendor may echo back an
    /// invalid one we sent), and any valid IBAN however a human spaced or cased it
    /// (<c>es79 2100 0813 …</c>). Anything that reaches a log, an audit entry or the UI goes
    /// through here first.
    /// </summary>
    public static string MaskAllIn(string? text)
    {
        if (string.IsNullOrEmpty(text)) return text ?? "";
        var masked = new StringBuilder(text.Length);
        var pos = 0;
        for (var m = IbanCandidate().Match(text); m.Success; m = IbanCandidate().Match(text, pos))
        {
            var length = LeadingIbanLength(m.Value);
            // No IBAN starts here: step past the candidate's first word and look again.
            var consumed = length > 0 ? length : FirstWordLength(m.Value);
            masked.Append(text, pos, m.Index - pos)
                .Append(length > 0 ? Mask(Compact(m.Value[..length])) : m.Value[..consumed]);
            pos = m.Index + consumed;
        }
        return masked.Append(text, pos, text.Length - pos).ToString();
    }

    // A candidate can run on into the words after the IBAN ("… 6789 please"), so the IBAN is its
    // longest separator-bounded prefix that qualifies. 0 when none does.
    private static int LeadingIbanLength(string candidate)
    {
        for (var end = candidate.Length; end > 4; end--)
        {
            if (end < candidate.Length && !IsSeparator(candidate[end])) continue;
            var compact = Compact(candidate[..end]);
            var unspaced = compact.Length == end;
            if ((unspaced && IbanShaped().IsMatch(compact)) || IbanValidator.IsValid(compact))
                return end;
        }
        return 0;
    }

    private static int FirstWordLength(string candidate)
    {
        var length = 0;
        while (length < candidate.Length && !IsSeparator(candidate[length])) length++;
        return length;
    }

    // Any whitespace (spaces, NBSP, tabs, line breaks — however the text wrapped) or a hyphen.
    private static bool IsSeparator(char c) => c == '-' || char.IsWhiteSpace(c);

    private static string Compact(string s) => string.Concat(s.Where(c => !IsSeparator(c)));

    // ISO 13616: 2 letters, 2 check digits, then 11–30 alphanumerics.
    [GeneratedRegex(@"^[A-Z]{2}[0-9]{2}[A-Z0-9]{11,30}$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex IbanShaped();

    // The same shape in any case, with separators between characters. Word-bounded, so a candidate
    // never starts or ends inside a longer token such as a Holded doc id or a hash.
    [GeneratedRegex(@"\b[A-Za-z]{2}[0-9]{2}(?:[\s-]*[A-Za-z0-9]){11,30}\b",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex IbanCandidate();
}
