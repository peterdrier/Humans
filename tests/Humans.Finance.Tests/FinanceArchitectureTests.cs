using System.Reflection;
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

    [HumansFact]
    public void NoFinanceControllerActionEscapesTheClassPolicy()
    {
        // An action-level [Authorize] only adds to the class policy; [AllowAnonymous] is the one
        // attribute that would drop it for a single action.
        typeof(FinanceController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m.GetCustomAttributes(inherit: true).OfType<IAllowAnonymous>().Any())
            .Select(m => m.Name)
            .Should().BeEmpty();
    }
}
