using System.ComponentModel.DataAnnotations;
using Humans.Expenses.Contracts;
using Humans.Finance.Contracts;
using Humans.Preapprovals.Contracts;
using Humans.Base.Helpers;
using NodaTime;

namespace Humans.Expenses.Models;

internal sealed class ExpensesIndexViewModel
{
    public required IReadOnlyList<ExpenseReportDto> Reports { get; init; }
    public bool HasActiveYear { get; init; }
    public bool HasIban { get; init; }
    /// <summary>The account, or legacy category, each report books to.</summary>
    public IReadOnlyDictionary<Guid, string> BookingLabels { get; init; } =
        new Dictionary<Guid, string>();

    /// <summary>Soonest deadline first.</summary>
    public IReadOnlyList<GrantOption> OpenGrants { get; init; } = [];
    /// <summary>Europe/Madrid, the zone claim deadlines are read in.</summary>
    public LocalDate Today { get; init; }

    /// <summary>Null both when unbound and when bound with no cached activity; <see cref="BoundAccountNum"/> tells
    /// them apart.</summary>
    public HoldedCreditorLedger? AccountLedger { get; init; }

    public int? BoundAccountNum { get; init; }

    /// <summary>Expected for a new account before its first journal entry; not an unresolved binding.</summary>
    public bool AwaitingFirstLedgerActivity => BoundAccountNum is not null && AccountLedger is null;

    /// <summary>Unbound although a report already reached Holded: auto-bind failed and only a manual bind clears it.
    /// Unbound with no pushed report is just "not yet".</summary>
    public bool CreditorBindingFailed =>
        BoundAccountNum is null && Reports.Any(r => r.HoldedDocIds.Count > 0);

    /// <summary>Regardless of queue depth.</summary>
    public bool IsCoordinator { get; init; }

    public int CoordinatorQueueCount { get; init; }
}

internal sealed class ExpenseNewViewModel
{
    public IReadOnlyList<HoldedExpenseAccountOption> Accounts { get; set; } = [];

    /// <summary>The service derives the budget category; the form never names one.</summary>
    [Range(1, int.MaxValue, ErrorMessage = "Validation_Required")]
    public int HoldedAccountNumber { get; set; }

    [StringLength(500, ErrorMessage = "Validation_MaxLength")]
    public string? Note { get; set; }

    /// <summary>Only a finance admin can post anyone but the viewer; the picker renders only for them.</summary>
    public Guid? SubmitterUserId { get; set; }

    /// <summary>When set, the service takes the account from the grant and ignores the posted one.</summary>
    public Guid? PreapprovalGrantId { get; set; }
    /// <summary>The viewer's claimable grants, plus the one <c>?grant=</c> preselected when it is not among them.</summary>
    public IReadOnlyList<GrantOption> Grants { get; set; } = [];
    public LocalDate Today { get; set; }
}

internal sealed class ExpenseEditViewModel
{
    public ExpenseReportDto? Report { get; set; }
    public IReadOnlyList<HoldedExpenseAccountOption> Accounts { get; set; } = [];

    [Range(1, int.MaxValue, ErrorMessage = "Validation_Required")]
    public int HoldedAccountNumber { get; set; }

    [StringLength(500, ErrorMessage = "Validation_MaxLength")]
    public string? Note { get; set; }

    /// <summary>Changeable on a Draft only.</summary>
    public Guid? PreapprovalGrantId { get; set; }
    /// <summary>The submitter's claimable grants plus the one the report already holds.</summary>
    public IReadOnlyList<GrantOption> Grants { get; set; } = [];
    public LocalDate Today { get; set; }
    /// <summary>For the deadline banner; null when none or missing.</summary>
    public GrantOption? HeldGrant => Report?.PreapprovalGrantId is { } id ? Grants.FirstOrDefault(g => g.Id == id) : null;
}

internal sealed class ExpenseDetailViewModel
{
    public required ExpenseReportDto Report { get; init; }
    public required string BookingLabel { get; init; }
    /// <summary>In whatever state it is now; null when none.</summary>
    public GrantOption? PreapprovalGrant { get; init; }
    public LocalDate Today { get; init; }
    public bool CanEdit { get; init; }
    public bool CanSubmit { get; init; }
    public bool CanWithdraw { get; init; }

    public bool IsSubmitter { get; init; }

    /// <summary>The submitter's profile IBAN state, not the viewer's. Loaded only for a viewer who may act on it.</summary>
    public bool HasIban { get; init; }
    public string? MaskedIban { get; init; }
    /// <summary>Mirrors what the Iban action accepts, so nobody is offered a button that 403s.</summary>
    public bool CanEditIban => IsSubmitter || CanEdit;

    /// <summary>The legal name shows unmasked, so no wider than the submitter and the finance admins who approve the
    /// payment.</summary>
    public bool CanSeePayee => IsSubmitter || CanBindCreditor;

    /// <summary>Submitter and reviewers (category coordinator, finance admin): wider than <see cref="CanSeePayee"/>,
    /// which also unlocks the IBAN. Set explicitly even though only those hold View.</summary>
    public bool CanSeeNames { get; init; }
    /// <summary>Null unless <see cref="CanSeeNames"/>.</summary>
    public string? SubmitterBurnerName { get; init; }
    public string? VisibleBurnerName => CanSeeNames ? SubmitterBurnerName : null;
    public string? VisibleLegalName => CanSeeNames ? SubmitterLegalName : null;
    /// <summary>The frozen snapshot once submitted, else their current profile.</summary>
    public string? SubmitterLegalName { get; init; }

    /// <summary>Frozen at submit: who Holded pays, whatever later profile edits say. Null on a draft. Not masked.</summary>
    public string? PayeeName =>
        CanSeePayee && !string.IsNullOrEmpty(Report.PayeeName) ? Report.PayeeName : null;

    /// <summary>The IBAN frozen at submit, masked.</summary>
    public string? PayeeMaskedIban =>
        CanSeePayee && !string.IsNullOrEmpty(Report.PayeeIban) ? IbanFormatter.Mask(Report.PayeeIban) : null;

    public string? LastRejectionReason => Report.LastRejectionReason;

    public ExpenseHoldedTimeline? HoldedTimeline { get; init; }

    public bool CanBindCreditor { get; init; }
    public int? BoundAccountNum { get; init; }
    /// <summary>Blank when Holded has no contact for it.</summary>
    public string? BoundAccountName { get; init; }
    /// <summary>With or without a 400000xx yet. False means the push will create one, the normal new-member state.</summary>
    public bool HasCreditorContact { get; init; }
    public IReadOnlyList<HoldedCreditorAccountRow> CreditorAccounts { get; init; } = [];

    public bool CanApprove { get; init; }
    /// <summary>Returns the report to the submitter as a draft.</summary>
    public bool CanFinanceReject { get; init; }
    public bool CanEndorse { get; init; }
    public bool CanCoordinatorReject { get; init; }

    public bool CanDeclineLines { get; init; }

    /// <summary>Empty unless <see cref="CanApprove"/>.</summary>
    public IReadOnlyList<HoldedExpenseAccountOption> Accounts { get; init; } = [];
}

internal sealed class ExpenseLineNewViewModel
{
    public required Guid ReportId { get; init; }
    /// <summary>The payee is a business invoicing the association (VAT-recoverable).</summary>
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
    [Required(ErrorMessage = "Validation_Required"), StringLength(500, ErrorMessage = "Validation_MaxLength")]
    public string Description { get; set; } = "";

    [Required(ErrorMessage = "Validation_Required"), Range(0.01, 1_000_000, ErrorMessage = "Validation_Range")]
    public decimal Amount { get; set; }

    /// <summary>The service rejects travel types on this path.</summary>
    public ExpenseLineType LineType { get; set; } = ExpenseLineType.Receipt;

    public Guid? ParentLineId { get; set; }
}

internal sealed class EditLineInputModel
{
    [Required(ErrorMessage = "Validation_Required")]
    public Guid LineId { get; set; }

    [Required(ErrorMessage = "Validation_Required"), StringLength(500, ErrorMessage = "Validation_MaxLength")]
    public string Description { get; set; } = "";

    [Required(ErrorMessage = "Validation_Required"), Range(0.01, 1_000_000, ErrorMessage = "Validation_Range")]
    public decimal Amount { get; set; }
}

internal sealed class CoordinatorRejectInputModel
{
    [Required, StringLength(1000, MinimumLength = 1)]
    public string Reason { get; set; } = "";
}

internal sealed class DeclineLineInputModel
{
    [Required, StringLength(1000, MinimumLength = 1)]
    public string Reason { get; set; } = "";
}

internal sealed class ExpenseReviewViewModel
{
    public required IReadOnlyList<ExpenseReportDto> Reports { get; init; }
    public required IReadOnlyDictionary<Guid, string> SubmitterNames { get; init; }
    public required IReadOnlyDictionary<Guid, string> BookingLabels { get; init; }
    /// <summary>Written-off pushes. Empty hides the banner and the Holded column.</summary>
    public required IReadOnlySet<Guid> FailedHoldedPushReportIds { get; init; }
    /// <summary>Empty hides the column; a revoked grant flags its row from the moment of revocation.</summary>
    public required IReadOnlyDictionary<Guid, GrantOption> Grants { get; init; }
    /// <summary>Shows the Creditor column, linking to each report's creditor statement.</summary>
    public bool IsFinanceAdmin { get; init; }

    public bool IsRevoked(Guid reportId) =>
        Grants.TryGetValue(reportId, out var grant) && grant.Status == GrantStatus.Revoked;

    /// <summary>One table per status, in workflow order.</summary>
    public IEnumerable<IGrouping<ExpenseReportStatus, ExpenseReportDto>> ByStatus =>
        Reports.GroupBy(r => r.Status).OrderBy(g => g.Key);
}

internal sealed class ApproveInputModel
{
    /// <summary>Rebooks the report.</summary>
    public int? OverrideAccountNumber { get; set; }

    /// <summary>Overrides whatever the coordinator authorized at endorsement.</summary>
    [Range(0.01, 1_000_000)]
    public decimal? MaxAmount { get; set; }
}

internal sealed class EndorseInputModel
{
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

    /// <summary>Set when it is not the viewer's own, so an admin sees whose account they are typing.</summary>
    public string? MemberName { get; set; }

    /// <summary>Decides whether removal is still on offer.</summary>
    public ExpenseReportStatus ReportStatus { get; set; }

    /// <summary>A report awaiting payment still needs an IBAN, so the service refuses to clear one.</summary>
    public bool CanRemoveIban =>
        ReportStatus is not (ExpenseReportStatus.Submitted or ExpenseReportStatus.CoordinatorEndorsed);

    [StringLength(34, ErrorMessage = "Validation_MaxLength")]
    public string? Iban { get; set; }
}

internal sealed record PreapprovalBannerModel(GrantOption Grant, LocalDate Today, bool ShowDeadline)
{
    /// <summary>Negative once it has passed.</summary>
    public int DaysLeft => Period.DaysBetween(Today, Grant.ClaimBy);
    /// <summary>Filed in time and holds the grant: the deadline no longer applies.</summary>
    public bool FiledInTime => Grant.Status == GrantStatus.Claimed;
    /// <summary>The report can no longer be approved against the grant.</summary>
    public bool Revoked => Grant.Status == GrantStatus.Revoked;
    public bool Urgent => Revoked || (ShowDeadline && !FiledInTime && DaysLeft <= 7);
}

internal sealed record PreapprovalSelectModel(IReadOnlyList<GrantOption> Grants, Guid? SelectedGrantId, string AccountSelectId);
