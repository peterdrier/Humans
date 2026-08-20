using AwesomeAssertions;
using Humans.Expenses.Contracts;
using Humans.Expenses.Models;
using Humans.Finance.Contracts;
using NodaTime;

namespace Humans.Expenses.Tests.Models;

/// <summary>
/// `GetCreditorLedgerAsync` returns null both for "not bound" and for "bound, but Holded has booked
/// nothing yet", so the page cannot infer the binding from the ledger alone. Conflating them tells a
/// correctly-bound member to go ask Finance to bind them again.
/// </summary>
public class ExpensesIndexViewModelTests
{
    private static ExpensesIndexViewModel Vm(
        int? boundAccountNum, HoldedCreditorLedger? ledger, params string?[] reportHoldedDocIds) =>
        new()
        {
            Reports = [.. reportHoldedDocIds.Select(Report)],
            BoundAccountNum = boundAccountNum,
            AccountLedger = ledger
        };

    private static ExpenseReportDto Report(string? holdedDocId) => new()
    {
        Id = Guid.NewGuid(),
        SubmitterUserId = Guid.NewGuid(),
        BudgetCategoryId = Guid.NewGuid(),
        BudgetYearId = Guid.NewGuid(),
        Status = ExpenseReportStatus.Approved,
        PayeeName = "Test Payee",
        PayeeIban = "ES9121000418450200051332",
        Total = 100m,
        HoldedDocId = holdedDocId,
        CreatedAt = Instant.MinValue,
        UpdatedAt = Instant.MinValue,
        Lines = []
    };

    [HumansFact]
    public void Bound_with_no_cached_lines_is_awaiting_activity_not_unbound()
    {
        var vm = Vm(boundAccountNum: 40000004, ledger: null);

        vm.AwaitingFirstLedgerActivity.Should().BeTrue();
    }

    [HumansFact]
    public void Unbound_is_not_awaiting_activity()
    {
        var vm = Vm(boundAccountNum: null, ledger: null);

        vm.AwaitingFirstLedgerActivity.Should().BeFalse();
    }

    [HumansFact]
    public void A_rendered_statement_is_not_awaiting_activity()
    {
        var ledger = new HoldedCreditorLedger(
            SupplierAccountNum: 40000004, Balance: -23m, OwedToMember: 23m, Lines: []);

        var vm = Vm(boundAccountNum: 40000004, ledger: ledger);

        vm.AwaitingFirstLedgerActivity.Should().BeFalse();
    }

    [HumansFact]
    public void Unbound_with_a_report_already_in_holded_is_a_failed_binding()
    {
        var vm = Vm(boundAccountNum: null, ledger: null, "doc-1");

        vm.CreditorBindingFailed.Should().BeTrue();
    }

    [HumansFact]
    public void Unbound_with_nothing_pushed_to_holded_is_not_a_failed_binding()
    {
        var vm = Vm(boundAccountNum: null, ledger: null, [null]);

        vm.CreditorBindingFailed.Should().BeFalse();
    }

    [HumansFact]
    public void Bound_is_never_a_failed_binding()
    {
        var vm = Vm(boundAccountNum: 40000004, ledger: null, "doc-1");

        vm.CreditorBindingFailed.Should().BeFalse();
    }
}
