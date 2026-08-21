using System.ComponentModel.DataAnnotations;
using Humans.Expenses.Services.Dtos;

namespace Humans.Expenses.Models;

internal sealed class CommitmentIndexViewModel
{
    public required IReadOnlyList<VendorCommitmentDto> Commitments { get; init; }
    public required IReadOnlyDictionary<Guid, string> CategoryNames { get; init; }
    /// <summary>Hides the "Match against Holded" button where no API key is configured.</summary>
    public required bool HoldedConfigured { get; init; }

    public int PendingReviewCount => Commitments.Sum(c => c.PendingCandidates.Count);
    public int AwaitingInvoiceCount => Commitments.Count(c => c.IsPaidAwaitingInvoice);
}

internal sealed class CommitmentAwaitingInvoiceViewModel
{
    /// <summary>Already ordered by age × amount — worst liability first.</summary>
    public required IReadOnlyList<VendorCommitmentDto> Commitments { get; init; }
    public required IReadOnlyDictionary<Guid, string> CategoryNames { get; init; }

    public decimal TotalOutstanding => Commitments.Sum(c => c.TotalPaid);
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
