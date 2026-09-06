using AwesomeAssertions;
using Humans.Finance.Contracts;
using Humans.Finance.Controllers;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Finance.Tests;

/// <summary>
/// Architecture tests enforcing the section shape for Finance.
/// </summary>
public class FinanceArchitectureTests
{
    [HumansFact]
    public void ContractsDoNotReExposeTheHoldedConnector()
    {
        // The Holded HTTP client belongs to the Holded section and is consumed by
        // Expenses as well as Finance. This leaf still may not name Humans.Application or
        // Humans.Domain, which is why HoldedCreditorLedger.Lines carries Finance's own
        // CreditorLedgerLine instead of the connector's HoldedLedgerLineDto.
        typeof(IHoldedFinanceService).Assembly.GetReferencedAssemblies()
            .Should().NotContain(a => a.Name == "Humans.Application" || a.Name == "Humans.Domain",
                because: "a section's contracts leaf references only the bottom of the graph "
                       + "(memory/architecture/section-project-cycle-fix.md)");
    }

    [HumansFact]
    public void FinanceControllerRequiresFinanceAdminOrAdmin()
    {
        // Nothing proves the negative at runtime: the render tests only ever sign in as Admin.
        typeof(FinanceController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .Single().Policy
            .Should().Be("FinanceAdminOrAdmin");
    }
}
