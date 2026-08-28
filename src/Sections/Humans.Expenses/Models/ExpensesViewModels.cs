using System.ComponentModel.DataAnnotations;
using Humans.Expenses.Contracts;
using Humans.Finance.Contracts;
using Humans.Base.Helpers;

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

    /// <summary>Unbound although a report already reached Holded — auto-bind failed and only a manual
    /// bind clears it (nobodies-collective/Humans#972). Unbound with no pushed report is just "not yet".</summary>
    public bool CreditorBindingFailed =>
        BoundAccountNum is null && Reports.Any(r => r.HoldedDocIds.Count > 0);

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

    /// <summary>The member the report will belong to. Defaults to the viewer; only a finance admin
    /// can post anything else, and the picker that changes it renders only for them.</summary>
    public Guid? SubmitterUserId { get; set; }
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

    /// <summary>The *report submitter's* current profile IBAN state — who this report will pay, not
    /// who is looking at it. Loaded only for a viewer who may act on it (<see cref="CanEditIban"/>).</summary>
    public bool HasIban { get; init; }
    public string? MaskedIban { get; init; }
    /// <summary>Mirrors what the Iban action accepts: the submitter at any status, or a finance admin
    /// filing on their behalf. Anyone else offered the button would get a guaranteed 403.</summary>
    public bool CanEditIban => IsSubmitter || CanEdit;

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

    /// <summary>Finance approval is available: the viewer may approve and the report is still pending.</summary>
    public bool CanApprove { get; init; }
    /// <summary>Finance rejection is available — returns the report to the submitter as a draft.</summary>
    public bool CanFinanceReject { get; init; }
    /// <summary>Coordinator endorsement is available (Submitted reports in a category they coordinate).</summary>
    public bool CanEndorse { get; init; }
    /// <summary>Coordinator rejection is available, under the same conditions as <see cref="CanEndorse"/>.</summary>
    public bool CanCoordinatorReject { get; init; }

    /// <summary>Budget categories offered by the approval form's override; empty unless <see cref="CanApprove"/>.</summary>
    public IReadOnlyList<BudgetCategoryOption> Categories { get; init; } = [];
}

internal sealed class ExpenseLineNewViewModel
{
    public required Guid ReportId { get; init; }
    /// <summary>Invoice mode: the payee is a business invoicing the association (VAT-recoverable).</summary>
    public required bool IsInvoice { get; init; }
}

internal sealed class ExpenseLineEditViewModel
{
    public required ExpenseReportDto Report { get; init; }
    public required ExpenseLineDto Line { get; init; }
    public required bool CanEditLines { get; init; }
}

internal sealed class ExpenseLineProofsViewModel
{
    public required ExpenseReportDto Report { get; init; }
    public required ExpenseLineDto InvoiceLine { get; init; }
    public required IReadOnlyList<ExpenseLineDto> Proofs { get; init; }
    public required bool CanEditLines { get; init; }
    public decimal ProofTotal => Proofs.Sum(p => p.Amount);
}

internal sealed class AddLineInputModel
{
    [Required, StringLength(500)]
    public string Description { get; set; } = "";

    [Required, Range(0.01, 1_000_000)]
    public decimal Amount { get; set; }

    /// <summary>Receipt (default) or Invoice; the service rejects travel types on this path.</summary>
    public ExpenseLineType LineType { get; set; } = ExpenseLineType.Receipt;

    /// <summary>Set when adding a proof row under an invoice line.</summary>
    public Guid? ParentLineId { get; set; }
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

internal sealed class CoordinatorRejectInputModel
{
    [Required, StringLength(1000, MinimumLength = 1)]
    public string Reason { get; set; } = "";
}

internal sealed class ExpenseReviewViewModel
{
    public required IReadOnlyList<ExpenseReportDto> Reports { get; init; }
    public required IReadOnlyDictionary<Guid, string> SubmitterNames { get; init; }
    /// <summary>Budget category id → department. Department-group categories are one per team, so
    /// the category name is the department; other groups show as "Group / Category".</summary>
    public required IReadOnlyDictionary<Guid, string> DepartmentNames { get; init; }
    /// <summary>Pushes to Holded that were written off and need a finance admin to look at them.
    /// Zero hides the banner.</summary>
    public required int FailedHoldedPushCount { get; init; }

    /// <summary>
    /// The viewer is a finance admin, so the page renders in the admin shell. Coordinators and
    /// members see the same queue, scoped to them, in the member shell — an admin sidebar filtered
    /// down to nothing is worse than no admin sidebar.
    /// </summary>
    public required bool IsAdminView { get; init; }

    /// <summary>Rows grouped for rendering: one table per status, in workflow order.</summary>
    public IEnumerable<IGrouping<ExpenseReportStatus, ExpenseReportDto>> ByStatus =>
        Reports.GroupBy(r => r.Status).OrderBy(g => g.Key);
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

    /// <summary>The member whose IBAN this is, when that is not the viewer — an admin setting it on
    /// their behalf must see whose account they are typing. Null when it is the viewer's own.</summary>
    public string? MemberName { get; set; }

    /// <summary>The status of the report this page was opened from — it decides whether removal
    /// is still on offer.</summary>
    public ExpenseReportStatus ReportStatus { get; set; }

    /// <summary>False once the report is submitted and awaiting payment: such a report still needs
    /// an IBAN, so the service refuses to clear one and the form must stop offering it.</summary>
    public bool CanRemoveIban =>
        ReportStatus is not (ExpenseReportStatus.Submitted or ExpenseReportStatus.CoordinatorEndorsed);

    [StringLength(34)]
    public string? Iban { get; set; }
}
