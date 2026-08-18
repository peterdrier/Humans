using AwesomeAssertions;
using Humans.Finance.Contracts;
using Humans.Finance.Controllers;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Finance.Tests;

/// <summary>
/// Architecture tests enforcing the section shape for Finance
/// (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// Replaces <c>Humans.Application.Tests/Architecture/FinanceArchitectureTests.cs</c>. Its
/// namespace-pinning test is gone — the assembly boundary subsumes it (design §15 step 11) —
/// and so is its "does not reference EF Core" test, which asserted a property of
/// <c>Humans.Application</c> that no longer says anything about this section: the section
/// project references EF Core because its repository lives in it. The rule that matters,
/// "only the repository touches the DbSets", is the universal HUM0025 analyzer's job.
/// </remarks>
public class FinanceArchitectureTests
{
    [HumansFact]
    public void ContractsDoNotReExposeTheHoldedConnector()
    {
        // The Holded HTTP client belongs to the Holded section (G5 lane 4b-2f) and is consumed by
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
        // Moved from Humans.Application.Tests' EndpointAuthorizationTests, which sweeps Shell's
        // controllers and can no longer name this one by type. Nothing proves the negative at
        // runtime: the render tests only ever sign in as Admin.
        typeof(FinanceController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .Single().Policy
            .Should().Be("FinanceAdminOrAdmin");
    }
}
