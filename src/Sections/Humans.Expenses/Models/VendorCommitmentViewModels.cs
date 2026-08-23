using System.ComponentModel.DataAnnotations;
using Humans.Expenses.Services.Dtos;
using NodaTime;

namespace Humans.Expenses.Models;

internal sealed class CommitmentIndexViewModel
{
    public required IReadOnlyList<VendorCommitmentDto> Commitments { get; init; }
    public required IReadOnlyDictionary<Guid, string> CategoryNames { get; init; }
    /// <summary>Hides the "Match against Holded" button where no API key is configured.</summary>
    public required bool HoldedConfigured { get; init; }

    /// <summary>Newest commitments on top — the registry's default reading order.</summary>
    public IReadOnlyList<VendorCommitmentDto> Newest =>
        Commitments.OrderByDescending(c => c.CreatedAt).ToList();

    public int PendingReviewCount => Commitments.Sum(c => c.PendingCandidates.Count);
    public int AwaitingInvoiceCount => Commitments.Count(c => c.IsPaidAwaitingInvoice);
}

internal sealed class CommitmentAwaitingInvoiceViewModel
{
    // Payments carry a LocalDate; the clock carries an Instant. Both have to land in one
    // calendar to be subtracted, and the association's is Madrid's — same zone the matching
    // run reads Holded document dates in.
    private static readonly DateTimeZone MadridZone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    public required IReadOnlyList<VendorCommitmentDto> Commitments { get; init; }
    public required IReadOnlyDictionary<Guid, string> CategoryNames { get; init; }
    /// <summary>Anchors the age half of the liability sort.</summary>
    public required Instant Now { get; init; }

    public decimal TotalOutstanding => Commitments.Sum(c => c.TotalPaid);

    /// <summary>Worst liability first, so an old six-figure hole outranks a fresh small one.</summary>
    public IReadOnlyList<VendorCommitmentDto> ByLiability =>
        Commitments
            .OrderByDescending(c => LiabilityWeight(c, Now))
            .ThenByDescending(c => c.TotalPaid)
            .ToList();

    /// <summary>
    /// Age × euros outstanding. Age counts from the first payment out, since that is when the
    /// association started being owed an invoice — from <c>PaidOn</c>, the date the transfer
    /// actually left, not <c>CreatedAt</c>, the moment someone typed it in. A transfer made
    /// months ago and backfilled today is months old, and ranking it as a same-day liability
    /// would bury the overdue invoice this list exists to surface. Days are offset by one so
    /// the amount still discriminates on the day a payment is made; a bare multiply would
    /// score every same-day liability zero, tiny and six-figure alike.
    /// </summary>
    private static decimal LiabilityWeight(VendorCommitmentDto c, Instant now)
    {
        var oldest = c.Payments.Count == 0
            ? now.InZone(MadridZone).Date
            : c.Payments.Min(p => p.PaidOn);
        var days = (decimal)Math.Max(0, Period.DaysBetween(oldest, now.InZone(MadridZone).Date));
        return (days + 1m) * c.TotalPaid;
    }
}

internal sealed class CommitmentDetailViewModel
{
    public required VendorCommitmentDto Commitment { get; init; }
    public string? CategoryDisplayName { get; init; }

    public decimal Outstanding => Commitment.ExpectedAmount - Commitment.TotalPaid;
}

internal sealed class CommitmentNewViewModel
{
    public IReadOnlyList<BudgetCategoryOption> Categories { get; set; } = [];

    [Required, StringLength(200)]
    public string VendorName { get; set; } = "";

    [Required, Range(0.01, 10_000_000)]
    public decimal ExpectedAmount { get; set; }

    [Required, StringLength(500)]
    public string Purpose { get; set; } = "";

    public Guid? BudgetCategoryId { get; set; }
}

internal sealed class RecordCommitmentPaymentInputModel
{
    [Required, Range(0.01, 10_000_000)]
    public decimal Amount { get; set; }

    [Required]
    public DateOnly PaidOn { get; set; }

    [StringLength(200)]
    public string? Reference { get; set; }
}
