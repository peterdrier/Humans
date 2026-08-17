using System.ComponentModel.DataAnnotations;
using Humans.Expenses.Contracts;
using Humans.Expenses.Services;
using Humans.Expenses.Services.Dtos;
using Humans.Finance.Contracts;
using Humans.Domain.Enums;
using Humans.Domain.Helpers;
using NodaTime;
using Humans.Expenses.Domain;

namespace Humans.Expenses.Models;

internal sealed class ExpensesIndexViewModel
{
    public required IReadOnlyList<ExpenseReportDto> Reports { get; init; }
    public bool HasActiveYear { get; init; }
    public bool HasIban { get; init; }
    public IReadOnlyDictionary<Guid, string> CategoryNames { get; init; } =
        new Dictionary<Guid, string>();

    /// <summary>The member's own Holded creditor account statement (real ledger lines), once bound. Read-only.
    /// Null both when unbound and when bound with no cached journal activity — <see cref="BoundAccountNum"/>
    /// is what separates the two.</summary>
    public HoldedCreditorLedger? AccountLedger { get; init; }

    /// <summary>The member's bound 400000xx account, or null if they have no binding yet.</summary>
    public int? BoundAccountNum { get; init; }

    /// <summary>Bound, but Holded has booked nothing to the account yet — expected for a new account
    /// before its first journal entry, and not the same thing as an unresolved binding.</summary>
    public bool AwaitingFirstLedgerActivity => BoundAccountNum is not null && AccountLedger is null;

    /// <summary>True when this user is a coordinator for any budget-year team, regardless of queue depth.</summary>
    public bool IsCoordinator { get; init; }

    /// <summary>Reports awaiting this member as camp coordinator; surfaces the queue entry point.</summary>
    public int CoordinatorQueueCount { get; init; }
}

internal sealed class ExpenseNewViewModel
{
    public IReadOnlyList<BudgetCategoryOption> Categories { get; set; } = [];

    [Required]
    public Guid BudgetCategoryId { get; set; }

    [StringLength(500)]
    public string? Note { get; set; }
}

internal sealed class ExpenseEditViewModel
{
    public ExpenseReportDto? Report { get; set; }
    public IReadOnlyList<BudgetCategoryOption> Categories { get; set; } = [];
    public bool CanEditHeader { get; set; }
    public bool CanEditLines { get; set; }

    public Guid BudgetCategoryId { get; set; }

    [StringLength(500)]
    public string? Note { get; set; }
}

internal sealed record BudgetCategoryOption(Guid Id, string GroupName, string CategoryName)
{
    public string DisplayName => $"{GroupName} / {CategoryName}";
}

internal sealed class ExpenseDetailViewModel
{
    public required ExpenseReportDto Report { get; init; }
    public required string CategoryDisplayName { get; init; }
    public bool CanEdit { get; init; }
    public bool CanSubmit { get; init; }
    public bool CanWithdraw { get; init; }

    public bool IsSubmitter { get; init; }

    /// <summary>The viewer's own profile IBAN state. Only meaningful while they are still setting it up
    /// on their own draft — a viewer's IBAN says nothing about someone else's report.</summary>
    public bool HasIban { get; init; }
    public string? MaskedIban { get; init; }
    /// <summary>The Set/Change IBAN actions reject everyone but the submitter, so only they get the button.</summary>
    public bool CanEditIban => IsSubmitter;

    /// <summary>Payee identity is snapshotted at submit, and the legal name shows unmasked — so it goes
    /// no wider than the submitter and the finance admins who approve the payment.</summary>
    public bool CanSeePayee => IsSubmitter || CanBindCreditor;

    /// <summary>Legal name frozen onto the report at submit — who Holded will actually pay, regardless
    /// of later profile edits. Null on a draft (not yet snapshotted). Not masked.</summary>
    public string? PayeeName =>
        CanSeePayee && !string.IsNullOrEmpty(Report.PayeeName) ? Report.PayeeName : null;

    /// <summary>Masked form of the IBAN frozen onto the report at submit.</summary>
    public string? PayeeMaskedIban =>
        CanSeePayee && !string.IsNullOrEmpty(Report.PayeeIban) ? IbanFormatter.Mask(Report.PayeeIban) : null;

    /// <summary>Non-null when the report was previously rejected.</summary>
    public string? LastRejectionReason => Report.LastRejectionReason;

    public ExpenseHoldedTimeline? HoldedTimeline { get; init; }

    /// <summary>True when the viewer is a finance admin who may bind the submitter to a Holded account.</summary>
    public bool CanBindCreditor { get; init; }
    /// <summary>The submitter's currently-bound 400000xx account, or null if unbound.</summary>
    public int? BoundAccountNum { get; init; }
    /// <summary>Holded's name for <see cref="BoundAccountNum"/>; blank when Holded has no contact for it.</summary>
    public string? BoundAccountName { get; init; }
    /// <summary>True when the submitter already has a Holded creditor contact — with or without a
    /// 400000xx number yet. False means the push will create one, which is the expected new-member state.</summary>
    public bool HasCreditorContact { get; init; }
    /// <summary>Creditor accounts available to bind to (finance-admin only).</summary>
    public IReadOnlyList<HoldedCreditorAccountRow> CreditorAccounts { get; init; } = [];
}

internal sealed class AddLineInputModel
{
    [Required, StringLength(500)]
    public string Description { get; set; } = "";

    [Required, Range(0.01, 1_000_000)]
    public decimal Amount { get; set; }
}

internal sealed class EditLineInputModel
{
    [Required]
    public Guid LineId { get; set; }

    [Required, StringLength(500)]
    public string Description { get; set; } = "";

    [Required, Range(0.01, 1_000_000)]
    public decimal Amount { get; set; }
}

internal sealed class ExpenseCoordinatorViewModel
{
    public required IReadOnlyList<ExpenseReportDto> Reports { get; init; }
    public required IReadOnlyDictionary<Guid, string> SubmitterNames { get; init; }
}

internal sealed class CoordinatorRejectInputModel
{
    [Required, StringLength(1000, MinimumLength = 1)]
    public string Reason { get; set; } = "";
}

internal sealed class ExpenseReviewViewModel
{
    public required IReadOnlyList<ExpenseReportDto> Reports { get; init; }
    public required IReadOnlyDictionary<Guid, string> SubmitterNames { get; init; }
    /// <summary>Pushes to Holded that were written off and need a finance admin to look at them.
    /// Zero hides the banner.</summary>
    public required int FailedHoldedPushCount { get; init; }
}

internal sealed class ApproveInputModel
{
    /// <summary>Optional category override applied at approval time.</summary>
    public Guid? OverrideCategoryId { get; set; }

    /// <summary>Optional payout cap; overrides whatever the coordinator authorized at endorsement.</summary>
    [Range(0.01, 1_000_000)]
    public decimal? MaxAmount { get; set; }
}

internal sealed class EndorseInputModel
{
    /// <summary>Optional payout cap authorized by the coordinator.</summary>
    [Range(0.01, 1_000_000)]
    public decimal? MaxAmount { get; set; }
}

internal sealed class FinanceRejectInputModel
{
    [Required, StringLength(1000, MinimumLength = 1)]
    public string Reason { get; set; } = "";
}

internal sealed class ExpenseIbanViewModel
{
    public Guid ReportId { get; set; }
    public string? MaskedIban { get; set; }
    public bool HasIban { get; set; }

    [StringLength(34)]
    public string? Iban { get; set; }
}
