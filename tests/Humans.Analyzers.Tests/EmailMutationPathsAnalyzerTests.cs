using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Humans.Testing;

namespace Humans.Analyzers.Tests;

public class EmailMutationPathsAnalyzerTests
{
    private const string InterfaceStubs = """
        namespace Humans.Application.Interfaces.Profiles
        {
            public interface IUserEmailService
            {
                System.Threading.Tasks.Task<bool> UpdateEmailAsync(System.Guid userId, string provider, string providerKey, string newEmail);
            }
        }

        namespace Humans.Application.Interfaces.Repositories
        {
            public interface IUserEmailRepository
            {
                System.Threading.Tasks.Task<bool> UpdateEmailAsync(System.Guid userId, string provider, string providerKey, string newEmail);
            }
        }
        """;

    private static bool IsHum0005(Microsoft.CodeAnalysis.Diagnostic d) =>
        string.Equals(d.Id, EmailMutationPathsAnalyzer.ServiceCallerDiagnosticId, System.StringComparison.Ordinal);

    private static bool IsHum0006(Microsoft.CodeAnalysis.Diagnostic d) =>
        string.Equals(d.Id, EmailMutationPathsAnalyzer.RepositoryCallerDiagnosticId, System.StringComparison.Ordinal);

    [HumansFact]
    public async Task Fires_HUM0005_when_service_called_from_non_AccountController()
    {
        var source = InterfaceStubs + """

            namespace Humans.Web.Controllers
            {
                public class SomeOtherController
                {
                    public async System.Threading.Tasks.Task Run(
                        Humans.Application.Interfaces.Profiles.IUserEmailService svc)
                    {
                        await svc.UpdateEmailAsync(System.Guid.Empty, "p", "k", "e@x");
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new EmailMutationPathsAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0005(d));
    }

    [HumansFact]
    public async Task Does_not_fire_when_service_called_from_AccountController()
    {
        var source = InterfaceStubs + """

            namespace Humans.Web.Controllers
            {
                public class AccountController
                {
                    public async System.Threading.Tasks.Task Run(
                        Humans.Application.Interfaces.Profiles.IUserEmailService svc)
                    {
                        await svc.UpdateEmailAsync(System.Guid.Empty, "p", "k", "e@x");
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new EmailMutationPathsAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Where(d => IsHum0005(d)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Fires_HUM0006_when_repository_called_from_non_UserEmailService()
    {
        var source = InterfaceStubs + """

            namespace Humans.Application.Services.Other
            {
                public class SomeOtherService
                {
                    public async System.Threading.Tasks.Task Run(
                        Humans.Application.Interfaces.Repositories.IUserEmailRepository repo)
                    {
                        await repo.UpdateEmailAsync(System.Guid.Empty, "p", "k", "e@x");
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new EmailMutationPathsAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0006(d));
    }

    [HumansFact]
    public async Task Does_not_fire_when_repository_called_from_UserEmailService()
    {
        var source = InterfaceStubs + """

            namespace Humans.Application.Services.Profile
            {
                public class UserEmailService
                {
                    public async System.Threading.Tasks.Task Run(
                        Humans.Application.Interfaces.Repositories.IUserEmailRepository repo)
                    {
                        await repo.UpdateEmailAsync(System.Guid.Empty, "p", "k", "e@x");
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new EmailMutationPathsAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(d => IsHum0006(d)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Fires_HUM0005_when_concrete_service_class_called_from_non_AccountController()
    {
        // Codex P2: a caller holding the concrete UserEmailService (rather than the
        // IUserEmailService interface) used to bypass the analyzer because the call's
        // ContainingType was the class, not the interface.
        var source = InterfaceStubs + """

            namespace Humans.Application.Services.Profile
            {
                public class UserEmailService : Humans.Application.Interfaces.Profiles.IUserEmailService
                {
                    public async System.Threading.Tasks.Task<bool> UpdateEmailAsync(
                        System.Guid userId, string provider, string providerKey, string newEmail)
                    {
                        await System.Threading.Tasks.Task.CompletedTask;
                        return false;
                    }
                }
            }

            namespace Humans.Web.Controllers
            {
                public class SomeOtherController
                {
                    public async System.Threading.Tasks.Task Run(
                        Humans.Application.Services.Profile.UserEmailService concrete)
                    {
                        await concrete.UpdateEmailAsync(System.Guid.Empty, "p", "k", "e@x");
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new EmailMutationPathsAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0005(d));
    }

    [HumansFact]
    public async Task Fires_HUM0006_when_concrete_repository_class_called_from_non_UserEmailService()
    {
        var source = InterfaceStubs + """

            namespace Humans.Infrastructure.Repositories.Profiles
            {
                public class UserEmailRepository : Humans.Application.Interfaces.Repositories.IUserEmailRepository
                {
                    public async System.Threading.Tasks.Task<bool> UpdateEmailAsync(
                        System.Guid userId, string provider, string providerKey, string newEmail)
                    {
                        await System.Threading.Tasks.Task.CompletedTask;
                        return false;
                    }
                }
            }

            namespace Humans.Application.Services.Other
            {
                public class Sneaky
                {
                    public async System.Threading.Tasks.Task Run(
                        Humans.Infrastructure.Repositories.Profiles.UserEmailRepository concrete)
                    {
                        await concrete.UpdateEmailAsync(System.Guid.Empty, "p", "k", "e@x");
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new EmailMutationPathsAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0006(d));
    }

    [HumansFact]
    public async Task Does_not_fire_outside_scope_assemblies()
    {
        var source = InterfaceStubs + """

            namespace Some.Domain.Code
            {
                public class Caller
                {
                    public async System.Threading.Tasks.Task Run(
                        Humans.Application.Interfaces.Profiles.IUserEmailService svc)
                    {
                        await svc.UpdateEmailAsync(System.Guid.Empty, "p", "k", "e@x");
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new EmailMutationPathsAnalyzer(),
            "Humans.Domain",
            source);

        diagnostics.Should().BeEmpty();
    }
}
