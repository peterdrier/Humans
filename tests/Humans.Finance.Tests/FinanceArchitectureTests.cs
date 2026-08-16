using AwesomeAssertions;
using Humans.Gdpr.Contracts;
using Humans.Finance.Contracts;
using Humans.Finance.Controllers;
using Humans.Finance.Data;
using Humans.Finance.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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
    public void FinanceControllerRequiresFinanceAdminOrAdmin()
    {
        // Moved from Humans.Application.Tests' EndpointAuthorizationTests, which sweeps Shell's
        // controllers and can no longer name this one by type.
        typeof(FinanceController)
            .GetCustomAttributes(typeof(AuthorizeAttribute), inherit: false)
            .Cast<AuthorizeAttribute>()
            .Single().Policy
            .Should().Be("FinanceAdminOrAdmin");
    }




}
