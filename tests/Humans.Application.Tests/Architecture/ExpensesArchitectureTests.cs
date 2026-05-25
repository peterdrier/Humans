using AwesomeAssertions;
using Humans.Application.Interfaces.Expenses;
using Humans.Application.Services.Expenses;

namespace Humans.Application.Tests.Architecture;

public class ExpensesArchitectureTests
{
    [HumansFact]
    public void IExpenseReportService_LivesIn_ExpensesNamespace()
    {
        typeof(IExpenseReportService).Namespace
            .Should().Be("Humans.Application.Interfaces.Expenses");
    }

    [HumansFact]
    public void ExpenseReportService_DoesNotReferenceEFCore()
    {
        var asm = typeof(ExpenseReportService).Assembly;
        asm.GetReferencedAssemblies()
            .Should().NotContain(a => a.Name == "Microsoft.EntityFrameworkCore");
    }
}
