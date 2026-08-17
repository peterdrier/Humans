using AwesomeAssertions;
using Humans.Expenses.Contracts;
using Humans.Expenses.Services.Dtos;
using Humans.Expenses.Domain;
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
        ExpenseReportDto report, bool isSubmitter, bool isFinanceAdmin) =>
        new()
        {
            Report = report,
            CategoryDisplayName = "Ops / Fuel",
            IsSubmitter = isSubmitter,
            CanBindCreditor = isFinanceAdmin,
        };

    [HumansFact]
    public void Finance_admin_sees_the_submitters_payee_snapshot_not_their_own()
    {
        var vm = Vm(Report(), isSubmitter: false, isFinanceAdmin: true);

        vm.PayeeName.Should().Be("Ada Lovelace");            // legal name, unmasked
        vm.PayeeMaskedIban.Should().NotBeNullOrEmpty();
        vm.PayeeMaskedIban.Should().NotContain("0200051332"); // masked, never the full account
    }

    [HumansFact]
    public void Submitter_sees_their_own_payee_snapshot()
    {
        var vm = Vm(Report(), isSubmitter: true, isFinanceAdmin: false);

        vm.PayeeName.Should().Be("Ada Lovelace");
        vm.PayeeMaskedIban.Should().NotBeNullOrEmpty();
    }

    [HumansFact]
    public void A_plain_viewer_sees_no_payee_identity()
    {
        // A coordinator endorses, but does not pay — the legal name is unmasked, so it stops here.
        var vm = Vm(Report(), isSubmitter: false, isFinanceAdmin: false);

        vm.CanSeePayee.Should().BeFalse();
        vm.PayeeName.Should().BeNull();
        vm.PayeeMaskedIban.Should().BeNull();
    }

    [HumansFact]
    public void A_draft_has_no_snapshot_yet_so_the_payee_fields_stay_null()
    {
        // PayeeName/PayeeIban are frozen onto the report by SubmitAsync; before that they are blank,
        // and blank must not render as an empty payee line.
        var vm = Vm(Report(payeeName: "", payeeIban: ""), isSubmitter: true, isFinanceAdmin: false);

        vm.PayeeName.Should().BeNull();
        vm.PayeeMaskedIban.Should().BeNull();
    }

    [HumansFact]
    public void Only_the_submitter_gets_the_iban_edit_action()
    {
        // The Iban action Forbids non-submitters, so offering them the button is a guaranteed 403.
        Vm(Report(), isSubmitter: true, isFinanceAdmin: false).CanEditIban.Should().BeTrue();
        Vm(Report(), isSubmitter: false, isFinanceAdmin: true).CanEditIban.Should().BeFalse();
    }

    [HumansFact]
    public void An_uncapped_report_pays_its_receipts_total()
    {
        Report(total: 100m, maxAmount: null).Payable.Should().Be(100m);
    }

    [HumansFact]
    public void A_cap_above_the_receipts_total_does_not_raise_the_payout()
    {
        Report(total: 100m, maxAmount: 150m).Payable.Should().Be(100m);
    }

    [HumansFact]
    public void A_cap_below_the_receipts_total_is_what_gets_paid()
    {
        Report(total: 100m, maxAmount: 60m).Payable.Should().Be(60m);
    }
}
