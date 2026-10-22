using AwesomeAssertions;
using Humans.Expenses.Contracts;

namespace Humans.Expenses.Tests;

/// <summary>
/// The section project references EF Core because its repository lives in it; "only the
/// repository touches the DbSets" is the universal HUM0025 analyzer's job.
/// </summary>
public class ExpensesArchitectureTests
{
    [HumansFact]
    public void ContractsDoNotReExposeTheHoldedConnector()
    {
        // ExpenseReportBackgroundProcessor uses the Holded connector types heavily. They belong
        // to Base's connector, and letting one into the contracts leaf creates a project cycle.
        typeof(IExpenseReportBackgroundProcessor).Assembly.GetReferencedAssemblies()
            .Should().NotContain(a => a.Name == "Humans.Application" || a.Name == "Humans.Domain",
                because: "a section's contracts leaf references only the bottom of the graph "
                       + "(memory/architecture/section-project-cycle-fix.md)");
    }
}
