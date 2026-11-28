using AwesomeAssertions;
using Humans.Expenses.Contracts;
using Humans.Expenses.Models;
using NodaTime;

namespace Humans.Expenses.Tests.Models;

/// <summary>
/// The payee card answers "who gets paid, and to which account" — a question about the *submitter*,
/// never the viewer. It used to render the viewer's own IBAN on someone else's report, so a finance
/// admin checking payment readiness got their own answer. These pin the split.
/// </summary>
public class ExpenseDetailViewModelTests
{
    private static ExpenseReportDto Report(
        string payeeName = "Ada Lovelace",
        string payeeIban = "ES9121000418450200051332",
        decimal total = 23m,
        decimal? maxAmount = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            SubmitterUserId = Guid.NewGuid(),
            BudgetYearId = Guid.NewGuid(),
            HoldedAccountNumber = 0,
            BudgetCategoryId = Guid.NewGuid(),
            Total = total,
            MaxAmount = maxAmount,
            Status = ExpenseReportStatus.Submitted,
            PayeeName = payeeName,
            PayeeIban = payeeIban,
            CreatedAt = Instant.FromUtc(2026, 5, 1, 0, 0),
            UpdatedAt = Instant.FromUtc(2026, 5, 1, 0, 0),
            Lines = [],
        };

    private static ExpenseDetailViewModel Vm(
        ExpenseReportDto report, bool isSubmitter, bool isFinanceAdmin, bool canEdit = false, bool canSeeNames = false) =>
        new()
        {
            CanSeeNames = canSeeNames,
            SubmitterLegalName = "Ada Lovelace",
            Report = report,
            BookingLabel = "Ops / Fuel",
            IsSubmitter = isSubmitter,
            CanBindCreditor = isFinanceAdmin,
            CanEdit = canEdit,
        };

    [HumansFact]
    public void Payee_FinanceAdmin_SeesSubmittersSnapshot()
    {
        var vm = Vm(Report(), isSubmitter: false, isFinanceAdmin: true);

        vm.PayeeName.Should().Be("Ada Lovelace");            // legal name, unmasked
        vm.PayeeMaskedIban.Should().NotBeNullOrEmpty();
        vm.PayeeMaskedIban.Should().NotContain("0200051332"); // masked, never the full account
    }

    [HumansFact]
    public void Payee_Submitter_SeesOwnSnapshot()
    {
        var vm = Vm(Report(), isSubmitter: true, isFinanceAdmin: false);

        vm.PayeeName.Should().Be("Ada Lovelace");
        vm.PayeeMaskedIban.Should().NotBeNullOrEmpty();
    }

    [HumansFact]
    public void Payee_PlainViewer_IsHidden()
    {
        // A coordinator endorses, but does not pay — the legal name is unmasked, so it stops here.
        var vm = Vm(Report(), isSubmitter: false, isFinanceAdmin: false);

        vm.CanSeePayee.Should().BeFalse();
        vm.PayeeName.Should().BeNull();
        vm.PayeeMaskedIban.Should().BeNull();
    }

    [HumansFact]
    public void Payee_Coordinator_SeesNamesNotIban()
    {
        var vm = Vm(Report(), isSubmitter: false, isFinanceAdmin: false, canSeeNames: true);

        vm.VisibleSubmitterUserId.Should().Be(vm.Report.SubmitterUserId);
        vm.VisibleLegalName.Should().Be("Ada Lovelace");
        vm.PayeeMaskedIban.Should().BeNull();
    }

    [HumansFact]
    public void Payee_Submitter_SeesNamesWithMaskedIban()
    {
        var vm = Vm(Report(), isSubmitter: true, isFinanceAdmin: false, canSeeNames: true);

        vm.VisibleSubmitterUserId.Should().Be(vm.Report.SubmitterUserId);
        vm.VisibleLegalName.Should().Be("Ada Lovelace");
        vm.PayeeMaskedIban.Should().NotBeNullOrEmpty().And.NotContain("0200051332");
    }

    [HumansFact]
    public void Payee_UnrelatedMember_IsHidden()
    {
        var vm = Vm(Report(), isSubmitter: false, isFinanceAdmin: false);

        vm.VisibleSubmitterUserId.Should().BeNull();
        vm.VisibleLegalName.Should().BeNull();
        vm.PayeeMaskedIban.Should().BeNull();
    }

    [HumansFact]
    public void Payee_Draft_IsNull()
    {
        // PayeeName/PayeeIban are frozen onto the report by SubmitAsync; before that they are blank,
        // and blank must not render as an empty payee line.
        var vm = Vm(Report(payeeName: "", payeeIban: ""), isSubmitter: true, isFinanceAdmin: false);

        vm.PayeeName.Should().BeNull();
        vm.PayeeMaskedIban.Should().BeNull();
    }

    [HumansFact]
    public void IbanEdit_SubmitterWithEditGrant_IsOffered()
    {
        // The Iban action Forbids anyone who is neither, so offering them the button is a
        // guaranteed 403. A finance admin gets there through the Edit grant, not the role.
        Vm(Report(), isSubmitter: true, isFinanceAdmin: false).CanEditIban.Should().BeTrue();
        Vm(Report(), isSubmitter: false, isFinanceAdmin: true, canEdit: true).CanEditIban.Should().BeTrue();
        Vm(Report(), isSubmitter: false, isFinanceAdmin: true).CanEditIban.Should().BeFalse();
    }

    [HumansFact]
    public void Payout_NoCap_IsReceiptsTotal()
    {
        Report(total: 100m, maxAmount: null).Payable.Should().Be(100m);
    }

    [HumansFact]
    public void Payout_CapAboveTotal_IsReceiptsTotal()
    {
        Report(total: 100m, maxAmount: 150m).Payable.Should().Be(100m);
    }

    [HumansFact]
    public void Payout_CapBelowTotal_IsCap()
    {
        Report(total: 100m, maxAmount: 60m).Payable.Should().Be(60m);
    }
}
