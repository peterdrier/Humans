using AwesomeAssertions;
using Humans.Expenses.Contracts;
using Humans.Expenses.Models;

namespace Humans.Expenses.Tests.Models;

/// <summary>
/// A report awaiting payment still needs an IBAN, so <c>SaveSubmitterIbanWithResultAsync</c>
/// refuses to clear one. The form must not offer what the service will refuse.
/// </summary>
public class ExpenseIbanViewModelTests
{
    [HumansTheory]
    [Xunit.InlineData(ExpenseReportStatus.Submitted)]
    [Xunit.InlineData(ExpenseReportStatus.CoordinatorEndorsed)]
    public void CanRemoveIban_IsFalse_WhileTheReportAwaitsPayment(ExpenseReportStatus status)
    {
        new ExpenseIbanViewModel { ReportStatus = status }.CanRemoveIban.Should().BeFalse();
    }

    [HumansTheory]
    [Xunit.InlineData(ExpenseReportStatus.Draft)]
    [Xunit.InlineData(ExpenseReportStatus.Approved)]
    [Xunit.InlineData(ExpenseReportStatus.Withdrawn)]
    public void CanRemoveIban_IsTrue_Otherwise(ExpenseReportStatus status)
    {
        new ExpenseIbanViewModel { ReportStatus = status }.CanRemoveIban.Should().BeTrue();
    }
}
