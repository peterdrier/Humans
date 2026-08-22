using System.Globalization;
using System.Text;
using Humans.Expenses.Domain;
using NodaTime;

namespace Humans.Expenses.Services;

/// <summary>A Holded purchase document reduced to the fields matching actually uses.</summary>
internal sealed record MatchableDocument(
    string Id, string DocNumber, string ContactName, LocalDate Date, decimal Total);

internal enum VendorCommitmentMatchDecision
{
    /// <summary>Nothing fits. The commitment stays on the paid-awaiting-invoice list.</summary>
    NoMatch,

    /// <summary>Exactly one document fits on amount and vendor — safe to link.</summary>
    Link,

    /// <summary>A human decides: either a tie, or a second document for an already-invoiced commitment.</summary>
    Review
}

/// <summary><paramref name="Linked"/> is set only for <see cref="VendorCommitmentMatchDecision.Link"/>;
/// <paramref name="ForReview"/> and <paramref name="ReviewKind"/> only for
/// <see cref="VendorCommitmentMatchDecision.Review"/>.</summary>
internal sealed record VendorCommitmentMatchOutcome(
    VendorCommitmentMatchDecision Decision,
    MatchableDocument? Linked,
    IReadOnlyList<MatchableDocument> ForReview,
    VendorCommitmentMatchKind ReviewKind);

/// <summary>
/// Pure commitment ↔ purchase-document matching (nobodies-collective/Humans#1030). Amount-first
/// and exact; the vendor name is a constraint, never a tie-break. Deliberately has no notion of
/// "closest" anything: every rule here either identifies a single document beyond doubt or hands
/// the decision to a human. Holded's own reconciliation UI not even amount-matching is what this
/// replaces.
/// </summary>
internal static class VendorCommitmentMatcher
{
    /// <summary>
    /// Below this many characters a normalized name is too generic for containment ("sa", "sl"
    /// would match every Spanish company), so it must match exactly instead.
    /// </summary>
    private const int MinContainmentLength = 4;

    /// <summary>
    /// Decides what to do with <paramref name="documents"/> for one commitment. The caller passes
    /// only documents not already linked to some other commitment.
    /// </summary>
    /// <param name="expectedAmount">The committed amount, matched exactly.</param>
    /// <param name="vendorName">The commitment's vendor, matched by normalized containment.</param>
    /// <param name="alreadyInvoiced">
    /// True when the commitment already carries a purchase document. Every further match is then a
    /// suspected duplicate and is flagged, never linked — the €120k proforma-and-invoice failure.
    /// </param>
    public static VendorCommitmentMatchOutcome Match(
        decimal expectedAmount,
        string vendorName,
        bool alreadyInvoiced,
        IReadOnlyList<MatchableDocument> documents)
    {
        var exact = documents.Where(d => d.Total == expectedAmount).ToList();
        if (exact.Count == 0) return NoMatch;

        // Checked before any narrowing: a second document for an invoiced commitment is a dupe
        // regardless of how well it fits, and must never be linked on its own (AC4).
        if (alreadyInvoiced)
            return new(VendorCommitmentMatchDecision.Review, null, exact,
                VendorCommitmentMatchKind.Duplicate);

        var pool = exact.Where(d => VendorMatches(vendorName, d.ContactName)).ToList();
        if (pool.Count == 0) return NoMatch;

        if (pool.Count == 1)
            return new(VendorCommitmentMatchDecision.Link, pool[0], [], default);

        // A tie. Nothing separates these documents that is not a guess — date proximity least of
        // all — so the queue gets them and the matcher stops (AC6).
        return new(VendorCommitmentMatchDecision.Review, null, pool,
            VendorCommitmentMatchKind.Ambiguous);
    }

    private static readonly VendorCommitmentMatchOutcome NoMatch =
        new(VendorCommitmentMatchDecision.NoMatch, null, [], default);

    /// <summary>
    /// True when the two names denote the same vendor. Containment either way, so the
    /// "Cruz Roja" / "Cruz Roja Española" mismatch that hid a €41k duplicate resolves, and so do
    /// trailing legal forms ("TOI TOI" / "TOI TOI, S.L.").
    /// </summary>
    public static bool VendorMatches(string? a, string? b)
    {
        var left = Normalize(a);
        var right = Normalize(b);
        if (left.Length == 0 || right.Length == 0) return false;
        if (string.Equals(left, right, StringComparison.Ordinal)) return true;

        var shorter = left.Length <= right.Length ? left : right;
        var longer = left.Length <= right.Length ? right : left;
        return shorter.Length >= MinContainmentLength
            && longer.Contains(shorter, StringComparison.Ordinal);
    }

    /// <summary>Lowercase, strip diacritics, drop everything that is not a letter or digit.</summary>
    public static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var decomposed = raw.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);
        foreach (var c in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        }
        return sb.ToString();
    }
}
