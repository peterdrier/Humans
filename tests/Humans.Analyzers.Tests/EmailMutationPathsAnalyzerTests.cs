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
                System.Threading.Tasks.Task<int> ReconcileOAuthIdentityAsync(System.Guid userId, string provider, string providerKey, string email, bool emailVerified);
            }
        }

        namespace Humans.Application.Interfaces.Repositories
        {
            public interface IUserEmailRepository
            {
                System.Threading.Tasks.Task ApplyReconcilePlanAsync(object? a, object? b, object? c, object? d);
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
                        await svc.ReconcileOAuthIdentityAsync(System.Guid.Empty, "p", "k", "e@x", true);
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
                        await svc.ReconcileOAuthIdentityAsync(System.Guid.Empty, "p", "k", "e@x", true);
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
                        await repo.ApplyReconcilePlanAsync(null, null, null, null);
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
                        await repo.ApplyReconcilePlanAsync(null, null, null, null);
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
                    public async System.Threading.Tasks.Task<int> ReconcileOAuthIdentityAsync(
                        System.Guid userId, string provider, string providerKey, string email, bool emailVerified)
                    {
                        await System.Threading.Tasks.Task.CompletedTask;
                        return 0;
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
                        await concrete.ReconcileOAuthIdentityAsync(System.Guid.Empty, "p", "k", "e@x", true);
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
                    public async System.Threading.Tasks.Task ApplyReconcilePlanAsync(
                        object? a, object? b, object? c, object? d)
                    {
                        await System.Threading.Tasks.Task.CompletedTask;
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
                        await concrete.ApplyReconcilePlanAsync(null, null, null, null);
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
    public async Task Fires_HUM0005_when_service_called_from_Infrastructure_non_AccountController()
    {
        // Positive scope test for Infrastructure — mirrors the HUM0006 canary below.
        // Same regression risk: a future scope narrowing from
        // IsApplicationWebOrInfrastructure to IsApplicationOrWeb would silently pass
        // every Application/Web HUM0005 test.
        var source = InterfaceStubs + """

            namespace Humans.Infrastructure.Jobs
            {
                public class SomeBackgroundJob
                {
                    public async System.Threading.Tasks.Task Run(
                        Humans.Application.Interfaces.Profiles.IUserEmailService svc)
                    {
                        await svc.ReconcileOAuthIdentityAsync(System.Guid.Empty, "p", "k", "e@x", true);
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new EmailMutationPathsAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0005(d));
    }

    [HumansFact]
    public async Task Fires_HUM0006_when_repository_called_from_Infrastructure_non_UserEmailService()
    {
        // Positive scope test for Infrastructure. The analyzer's
        // IsApplicationWebOrInfrastructure guard means a future narrowing of
        // that scope to ApplicationOrWeb would pass every test that lives in
        // Application/Web — this case is the canary for that regression.
        var source = InterfaceStubs + """

            namespace Humans.Infrastructure.Jobs
            {
                public class SomeBackgroundJob
                {
                    public async System.Threading.Tasks.Task Run(
                        Humans.Application.Interfaces.Repositories.IUserEmailRepository repo)
                    {
                        await repo.ApplyReconcilePlanAsync(null, null, null, null);
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new EmailMutationPathsAnalyzer(),
            "Humans.Infrastructure",
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
                        await svc.ReconcileOAuthIdentityAsync(System.Guid.Empty, "p", "k", "e@x", true);
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
