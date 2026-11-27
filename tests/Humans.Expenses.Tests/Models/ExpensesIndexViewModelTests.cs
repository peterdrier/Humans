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
        int? boundAccountNum, CreditorLedger? ledger, params string?[] reportHoldedDocIds) =>
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
        HoldedAccountNumber = 0,
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
    public void AwaitingActivity_BoundNoLines_IsTrue()
    {
        var vm = Vm(boundAccountNum: 40000004, ledger: null);

        vm.AwaitingFirstLedgerActivity.Should().BeTrue();
    }

    [HumansFact]
    public void AwaitingActivity_Unbound_IsFalse()
    {
        var vm = Vm(boundAccountNum: null, ledger: null);

        vm.AwaitingFirstLedgerActivity.Should().BeFalse();
    }

    [HumansFact]
    public void AwaitingActivity_RenderedStatement_IsFalse()
    {
        var ledger = new CreditorLedger(
            SupplierAccountNum: 40000004, Balance: -23m, OwedToMember: 23m, Lines: []);

        var vm = Vm(boundAccountNum: 40000004, ledger: ledger);

        vm.AwaitingFirstLedgerActivity.Should().BeFalse();
    }

    [HumansFact]
    public void FailedBinding_UnboundWithPushedReport_IsTrue()
    {
        var vm = Vm(boundAccountNum: null, ledger: null, "doc-1");

        vm.CreditorBindingFailed.Should().BeTrue();
    }

    [HumansFact]
    public void FailedBinding_UnboundNothingPushed_IsFalse()
    {
        var vm = Vm(boundAccountNum: null, ledger: null, [null]);

        vm.CreditorBindingFailed.Should().BeFalse();
    }

    [HumansFact]
    public void FailedBinding_Bound_IsFalse()
    {
        var vm = Vm(boundAccountNum: 40000004, ledger: null, "doc-1");

        vm.CreditorBindingFailed.Should().BeFalse();
    }
}
