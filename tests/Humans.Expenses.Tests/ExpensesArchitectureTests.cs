using AwesomeAssertions;
using Humans.Expenses.Contracts;

namespace Humans.Expenses.Tests;

/// <summary>
/// Architecture tests enforcing the section shape for Expenses
/// (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// The section project references EF Core because its repository lives in it; "only the
/// repository touches the DbSets" is the universal HUM0025 analyzer's job (design §15 step 11).
/// </remarks>
public class ExpensesArchitectureTests
{
    [HumansFact]
    public void ContractsDoNotReExposeTheHoldedConnector()
    {
        // ExpenseReportService.DrainHoldedOutboxAsync uses IHoldedClient,
        // HoldedPurchaseDocumentInput, HoldedAttachmentInput and HoldedTransientException
        // heavily. Those belong to the Base connector, which did not move
        // (memory/architecture/vendor-connectors-own-sections.md). Letting one into the
        // contracts leaf is the cycle Finance hit in A2 with HoldedLedgerLineDto.
        typeof(IExpenseReportBackgroundProcessor).Assembly.GetReferencedAssemblies()
            .Should().NotContain(a => a.Name == "Humans.Application" || a.Name == "Humans.Domain",
                because: "a section's contracts leaf references only the bottom of the graph "
                       + "(memory/architecture/section-project-cycle-fix.md)");
    }
}
