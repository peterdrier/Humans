using AwesomeAssertions;
using Humans.Application.Interfaces.Finance;
using Humans.Application.Services.Finance;

namespace Humans.Application.Tests.Architecture;

public class FinanceArchitectureTests
{
    [HumansFact]
    public void IHoldedFinanceService_LivesIn_FinanceNamespace()
    {
        typeof(IHoldedFinanceService).Namespace
            .Should().Be("Humans.Application.Interfaces.Finance");
    }

    [HumansFact]
    public void HoldedFinanceService_DoesNotReferenceEFCore()
    {
        var asm = typeof(HoldedFinanceService).Assembly;
        asm.GetReferencedAssemblies()
            .Should().NotContain(a => a.Name == "Microsoft.EntityFrameworkCore");
    }

    [HumansFact]
    public void HoldedFinanceService_Constructor_HasNoCrossSectionRepositories()
    {
        var ctor = typeof(HoldedFinanceService).GetConstructors().Single();
        var paramTypes = ctor.GetParameters().Select(p => p.ParameterType).ToList();
        // Allowed: own repository (IHoldedRepository), application services, IClock, ILogger.
        // Forbidden: any *Repository other than IHoldedRepository.
        var forbidden = paramTypes
            .Where(t => t.Name.EndsWith("Repository", StringComparison.Ordinal)
                     && !string.Equals(t.Name, "IHoldedRepository", StringComparison.Ordinal))
            .ToList();
        forbidden.Should().BeEmpty();
    }
}
