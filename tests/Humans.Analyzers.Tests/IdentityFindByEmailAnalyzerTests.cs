using System.Threading.Tasks;
using AwesomeAssertions;
using Humans.Testing;

namespace Humans.Analyzers.Tests;

public class IdentityFindByEmailAnalyzerTests
{
    private const string Stub = """
        namespace Humans.Domain.Entities
        {
            public class User { }
        }

        namespace Microsoft.AspNetCore.Identity
        {
            public class UserManager<TUser> where TUser : class
            {
                public System.Threading.Tasks.Task<TUser?> FindByEmailAsync(string email) =>
                    System.Threading.Tasks.Task.FromResult<TUser?>(null);
                public System.Threading.Tasks.Task<TUser?> FindByNameAsync(string name) =>
                    System.Threading.Tasks.Task.FromResult<TUser?>(null);
                public System.Threading.Tasks.Task<TUser?> FindByIdAsync(string id) =>
                    System.Threading.Tasks.Task.FromResult<TUser?>(null);
            }
        }
        """;

    private static bool IsHum0003(Microsoft.CodeAnalysis.Diagnostic d) =>
        string.Equals(d.Id, IdentityFindByEmailAnalyzer.DiagnosticId, System.StringComparison.Ordinal);

    [HumansFact]
    public async Task Fires_on_FindByEmailAsync_in_Application()
    {
        var source = Stub + """

            namespace Some.App.Code
            {
                public class Caller
                {
                    public async System.Threading.Tasks.Task Run(
                        Microsoft.AspNetCore.Identity.UserManager<Humans.Domain.Entities.User> mgr)
                    {
                        var u = await mgr.FindByEmailAsync("x@y");
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new IdentityFindByEmailAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0003(d));
    }

    [HumansFact]
    public async Task Fires_on_FindByNameAsync_in_Web()
    {
        var source = Stub + """

            namespace Some.Web.Code
            {
                public class Caller
                {
                    public async System.Threading.Tasks.Task Run(
                        Microsoft.AspNetCore.Identity.UserManager<Humans.Domain.Entities.User> mgr)
                    {
                        var u = await mgr.FindByNameAsync("alice");
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new IdentityFindByEmailAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0003(d));
    }

    [HumansFact]
    public async Task Does_not_fire_on_FindByIdAsync()
    {
        var source = Stub + """

            namespace Some.App.Code
            {
                public class Caller
                {
                    public async System.Threading.Tasks.Task Run(
                        Microsoft.AspNetCore.Identity.UserManager<Humans.Domain.Entities.User> mgr)
                    {
                        var u = await mgr.FindByIdAsync("123");
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new IdentityFindByEmailAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_outside_Application_or_Web()
    {
        var source = Stub + """

            namespace Some.Infra.Code
            {
                public class Caller
                {
                    public async System.Threading.Tasks.Task Run(
                        Microsoft.AspNetCore.Identity.UserManager<Humans.Domain.Entities.User> mgr)
                    {
                        var u = await mgr.FindByEmailAsync("x@y");
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new IdentityFindByEmailAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Should().BeEmpty();
    }
}
