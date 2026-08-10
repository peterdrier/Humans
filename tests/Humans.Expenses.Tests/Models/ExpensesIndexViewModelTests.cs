using AwesomeAssertions;
using Humans.Expenses.Models;
using Humans.Finance.Contracts;

namespace Humans.Expenses.Tests.Models;

/// <summary>
/// `GetCreditorLedgerAsync` returns null both for "not bound" and for "bound, but Holded has booked
/// nothing yet", so the page cannot infer the binding from the ledger alone. Conflating them tells a
/// correctly-bound member to go ask Finance to bind them again.
/// </summary>
public class ExpensesIndexViewModelTests
{
    private static ExpensesIndexViewModel Vm(int? boundAccountNum, HoldedCreditorLedger? ledger) =>
        new() { Reports = [], BoundAccountNum = boundAccountNum, AccountLedger = ledger };

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
            SupplierAccountNum: 40000004, Name: null, Balance: -23m, OwedToMember: 23m, Lines: []);

        var vm = Vm(boundAccountNum: 40000004, ledger: ledger);

        vm.AwaitingFirstLedgerActivity.Should().BeFalse();
    }
}
