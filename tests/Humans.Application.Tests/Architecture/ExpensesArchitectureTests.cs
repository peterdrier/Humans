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

    [HumansFact]
    public void ExpenseReportService_Constructor_HasNoCrossSectionRepositories()
    {
        var ctor = typeof(ExpenseReportService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();
        // Allowed: own repository, application services, IClock, ILogger.
        // Forbidden: any *Repository other than IExpenseRepository.
        var forbidden = paramTypes
            .Where(t => t.Name.EndsWith("Repository", StringComparison.Ordinal)
                     && !string.Equals(t.Name, "IExpenseRepository", StringComparison.Ordinal))
            .ToList();
        forbidden.Should().BeEmpty();
    }
}
