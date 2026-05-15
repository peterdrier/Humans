using System.ComponentModel.DataAnnotations;
using Humans.Application.Services.Expenses.Dtos;

namespace Humans.Web.Models;

// ─────────────────────────────────── Index ───────────────────────────────────

public sealed class ExpensesIndexViewModel
{
    public required IReadOnlyList<ExpenseReportDto> Reports { get; init; }
    public bool HasActiveYear { get; init; }
    public bool HasIban { get; init; }
    public IReadOnlyDictionary<Guid, string> CategoryNames { get; init; } =
        new Dictionary<Guid, string>();
}

// ─────────────────────────────────── New / Edit ──────────────────────────────

public sealed class ExpenseNewViewModel
{
    public IReadOnlyList<BudgetCategoryOption> Categories { get; set; } = [];

    // Bound on POST
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

    // Header edit fields (bound on POST)
    public Guid BudgetCategoryId { get; set; }

    [StringLength(500)]
    public string? Note { get; set; }
}

public sealed record BudgetCategoryOption(Guid Id, string GroupName, string CategoryName)
{
    public string DisplayName => $"{GroupName} / {CategoryName}";
}

// ─────────────────────────────────── Detail ──────────────────────────────────

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
}

// ────────────────────────── Add / edit line (inline form input) ──────────────

public sealed class AddLineInputModel
{
    [Required, StringLength(500)]
    public string Description { get; set; } = "";

    [Required, Range(0.01, 1_000_000)]
    public decimal Amount { get; set; }
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

// ─────────────────────────────── Coordinator queue ───────────────────────────

public sealed class ExpenseCoordinatorViewModel
{
    public required IReadOnlyList<ExpenseReportDto> Reports { get; init; }
    public required IReadOnlyDictionary<Guid, string> SubmitterNames { get; init; }
}

// ────────────────────────────── Coordinator actions ──────────────────────────

public sealed class CoordinatorRejectInputModel
{
    [Required, StringLength(1000, MinimumLength = 1)]
    public string Reason { get; set; } = "";
}

// ────────────────────────────────── Review queue ─────────────────────────────

public sealed class ExpenseReviewViewModel
{
    public required IReadOnlyList<ExpenseReportDto> Reports { get; init; }
    public required IReadOnlyDictionary<Guid, string> SubmitterNames { get; init; }
}

// ──────────────────────────── FinanceAdmin actions ───────────────────────────

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

// ─────────────────────────────────── IBAN modal ──────────────────────────────

public sealed class ExpenseIbanViewModel
{
    public Guid ReportId { get; set; }
    public string? MaskedIban { get; set; }
    public bool HasIban { get; set; }

    // Bound on POST
    [StringLength(34)]
    public string? Iban { get; set; }
}
