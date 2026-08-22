namespace Humans.Expenses.Domain;

/// <summary>Why a purchase document landed in the review queue instead of being linked.</summary>
internal enum VendorCommitmentMatchKind
{
    /// <summary>Several documents fit and nothing separates them — a human decides (AC6).</summary>
    Ambiguous,

    /// <summary>The commitment is already Invoiced and another document matches it (the TOI TOI failure).</summary>
    Duplicate
}
