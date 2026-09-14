using AwesomeAssertions;
using Humans.Finance.Controllers;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Finance.Tests;

/// <summary>
/// Architecture tests enforcing the section shape for Finance.
/// </summary>
public class FinanceArchitectureTests
{
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
