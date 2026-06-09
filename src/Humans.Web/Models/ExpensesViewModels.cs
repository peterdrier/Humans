using System.ComponentModel.DataAnnotations;
using Humans.Application.Interfaces.Expenses;
using Humans.Application.Services.Expenses.Dtos;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Web.Models;

public sealed class ExpenseIouSummary
{
    public required decimal OwedToMember { get; init; }
    public required decimal TotalPaid { get; init; }
    public required decimal OtherAmount { get; init; }
    public LocalDate? LastPaymentDate { get; init; }
}

/// <summary>One row in the combined reports-and-payments ledger, sorted by <see cref="Date"/> desc.</summary>
public sealed record ExpenseLedgerRow(
    LocalDate Date,
    bool IsPayment,
    string Label,
    decimal Amount,
    Guid? ReportId,
    ExpenseReportStatus? Status);

public sealed class ExpensesIndexViewModel
{
    public required IReadOnlyList<ExpenseReportDto> Reports { get; init; }
    public bool HasActiveYear { get; init; }
    public bool HasIban { get; init; }
    public IReadOnlyDictionary<Guid, string> CategoryNames { get; init; } =
        new Dictionary<Guid, string>();

    /// <summary>Non-null when the member has a Holded creditor account with activity.</summary>
    public ExpenseIouSummary? Iou { get; init; }
    public IReadOnlyList<ExpenseLedgerRow> Ledger { get; init; } = [];
}

public sealed class ExpenseNewViewModel
{
    public IReadOnlyList<BudgetCategoryOption> Categories { get; set; } = [];

    [Required]
    public Guid BudgetCategoryId { get; set; }

    [StringLength(500)]
    public string? Note { get; set; }
}

public sealed class ExpenseEditViewModel
{
    public ExpenseReportDto? Report { get; set; }
    public IReadOnlyList<BudgetCategoryOption> Categories { get; set; } = [];
    public bool CanEditHeader { get; set; }
    public bool CanEditLines { get; set; }

    public Guid BudgetCategoryId { get; set; }

    [StringLength(500)]
    public string? Note { get; set; }
}

public sealed record BudgetCategoryOption(Guid Id, string GroupName, string CategoryName)
{
    public string DisplayName => $"{GroupName} / {CategoryName}";
}

public sealed class ExpenseDetailViewModel
{
    public required ExpenseReportDto Report { get; init; }
    public required string CategoryDisplayName { get; init; }
    public bool CanEdit { get; init; }
    public bool CanSubmit { get; init; }
    public bool CanWithdraw { get; init; }
    public bool HasIban { get; init; }
    public string? MaskedIban { get; init; }

    /// <summary>Non-null when the report was previously rejected.</summary>
    public string? LastRejectionReason => Report.LastRejectionReason;

    public ExpenseHoldedTimeline? HoldedTimeline { get; init; }
}

public sealed class AddLineInputModel
{
    [Required, StringLength(500)]
    public string Description { get; set; } = "";

    [Required, Range(0.01, 1_000_000)]
    public decimal Amount { get; set; }
}

public sealed class AddMileageInputModel
{
    [Required, StringLength(200)]
    public string Origin { get; set; } = "";

    [Required, StringLength(200)]
    public string Destination { get; set; } = "";

    [Required, Range(0.1, 100_000)]
    public decimal Km { get; set; }
}

public sealed class AddPerDiemInputModel
{
    [Required]
    public PerDiemKind Kind { get; set; }

    [Required, Range(1, 366)]
    public int Days { get; set; }

    [StringLength(200)]
    public string? Note { get; set; }
}

public sealed class EditLineInputModel
{
    [Required]
    public Guid LineId { get; set; }

    [Required, StringLength(500)]
    public string Description { get; set; } = "";

    [Required, Range(0.01, 1_000_000)]
    public decimal Amount { get; set; }
}

public sealed class ExpenseCoordinatorViewModel
{
    public required IReadOnlyList<ExpenseReportDto> Reports { get; init; }
    public required IReadOnlyDictionary<Guid, string> SubmitterNames { get; init; }
}

public sealed class CoordinatorRejectInputModel
{
    [Required, StringLength(1000, MinimumLength = 1)]
    public string Reason { get; set; } = "";
}

public sealed class ExpenseReviewViewModel
{
    public required IReadOnlyList<ExpenseReportDto> Reports { get; init; }
    public required IReadOnlyDictionary<Guid, string> SubmitterNames { get; init; }
}

public sealed class ApproveInputModel
{
    /// <summary>Optional category override applied at approval time.</summary>
    public Guid? OverrideCategoryId { get; set; }
}

public sealed class FinanceRejectInputModel
{
    [Required, StringLength(1000, MinimumLength = 1)]
    public string Reason { get; set; } = "";
}

public sealed class ExpenseIbanViewModel
{
    public Guid ReportId { get; set; }
    public string? MaskedIban { get; set; }
    public bool HasIban { get; set; }

    [StringLength(34)]
    public string? Iban { get; set; }
}
