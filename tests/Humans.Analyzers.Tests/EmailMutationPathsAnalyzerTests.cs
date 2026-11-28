using AwesomeAssertions;

namespace Humans.Analyzers.Tests;

public class EmailMutationPathsAnalyzerTests
{
    private const string InterfaceStubs = """
        namespace Humans.Users.Contracts
        {
            public interface IUserEmailService
            {
                System.Threading.Tasks.Task<int> ReconcileOAuthIdentityAsync(System.Guid userId, string provider, string providerKey, string email, bool emailVerified);
            }
        }

        namespace Humans.Users.Data.Repositories
        {
            public interface IUserRepository
            {
                System.Threading.Tasks.Task ApplyUserEmailReconcilePlanAsync(object? a, object? b, object? c, object? d);
            }
        }
        """;

    private static bool IsHum0005(Microsoft.CodeAnalysis.Diagnostic d) =>
        string.Equals(d.Id, EmailMutationPathsAnalyzer.ServiceCallerDiagnosticId, StringComparison.Ordinal);

    private static bool IsHum0006(Microsoft.CodeAnalysis.Diagnostic d) =>
        string.Equals(d.Id, EmailMutationPathsAnalyzer.RepositoryCallerDiagnosticId, StringComparison.Ordinal);

    [HumansFact]
    public async Task Analyze_ServiceFromOtherCaller_FiresHUM0005()
    {
        var source = InterfaceStubs + """

            namespace Humans.Web.Controllers
            {
                public class SomeOtherController
                {
                    public async System.Threading.Tasks.Task Run(
                        Humans.Users.Contracts.IUserEmailService svc)
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
    public async Task Analyze_ServiceFromExternalLoginService_IsSilent()
    {
        var source = InterfaceStubs + """

            namespace Humans.Users.Services
            {
                public class ExternalLoginService
                {
                    public async System.Threading.Tasks.Task Run(
                        Humans.Users.Contracts.IUserEmailService svc)
                    {
                        await svc.ReconcileOAuthIdentityAsync(System.Guid.Empty, "p", "k", "e@x", true);
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new EmailMutationPathsAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0005).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Analyze_ServiceFromAccountController_FiresHUM0005()
    {
        // The pin moved off AccountController when HUM0031 forced the OAuth
        // decision ladder into ExternalLoginService. The controller
        // reaching back for the primitive must now fail the build — otherwise
        // the logic could drift back into the controller unnoticed.
        var source = InterfaceStubs + """

            namespace Humans.Web.Controllers
            {
                public class AccountController
                {
                    public async System.Threading.Tasks.Task Run(
                        Humans.Users.Contracts.IUserEmailService svc)
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
    public async Task Analyze_RepositoryFromOtherCaller_FiresHUM0006()
    {
        var source = InterfaceStubs + """

            namespace Humans.Application.Services.Other
            {
                public class SomeOtherService
                {
                    public async System.Threading.Tasks.Task Run(
                        Humans.Users.Data.Repositories.IUserRepository repo)
                    {
                        await repo.ApplyUserEmailReconcilePlanAsync(null, null, null, null);
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
    public async Task Analyze_RepositoryFromUserEmailService_IsSilent()
    {
        var source = InterfaceStubs + """

            namespace Humans.Users.Services
            {
                public class UserEmailService
                {
                    public async System.Threading.Tasks.Task Run(
                        Humans.Users.Data.Repositories.IUserRepository repo)
                    {
                        await repo.ApplyUserEmailReconcilePlanAsync(null, null, null, null);
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new EmailMutationPathsAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0006).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Analyze_RepositoryFromUserService_IsSilent()
    {
        var source = InterfaceStubs + """

            namespace Humans.Users.Services
            {
                public class UserService
                {
                    public async System.Threading.Tasks.Task Run(
                        Humans.Users.Data.Repositories.IUserRepository repo)
                    {
                        await repo.ApplyUserEmailReconcilePlanAsync(null, null, null, null);
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new EmailMutationPathsAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0006).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Analyze_ConcreteServiceOtherCaller_FiresHUM0005()
    {
        // A caller holding the concrete UserEmailService (rather than the
        // IUserEmailService interface) used to bypass the analyzer because the call's
        // ContainingType was the class, not the interface.
        var source = InterfaceStubs + """

            namespace Humans.Users.Services
            {
                public class UserEmailService : Humans.Users.Contracts.IUserEmailService
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
                        Humans.Users.Services.UserEmailService concrete)
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
    public async Task Analyze_ConcreteRepositoryOtherCaller_FiresHUM0006()
    {
        var source = InterfaceStubs + """

            namespace Humans.Infrastructure.Repositories.Users
            {
                public class UserRepository : Humans.Users.Data.Repositories.IUserRepository
                {
                    public async System.Threading.Tasks.Task ApplyUserEmailReconcilePlanAsync(
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
                        Humans.Infrastructure.Repositories.Users.UserRepository concrete)
                    {
                        await concrete.ApplyUserEmailReconcilePlanAsync(null, null, null, null);
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
    public async Task Analyze_ServiceFromInfrastructureCaller_FiresHUM0005()
    {
        // Positive scope test for Infrastructure — mirrors the HUM0006 canary below.
        // The rule is unscoped; this pins that a caller outside Application/Web
        // is still checked.
        var source = InterfaceStubs + """

            namespace Humans.Infrastructure.Jobs
            {
                public class SomeBackgroundJob
                {
                    public async System.Threading.Tasks.Task Run(
                        Humans.Users.Contracts.IUserEmailService svc)
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
    public async Task Analyze_RepositoryFromInfrastructureCaller_FiresHUM0006()
    {
        // Positive scope test for Infrastructure. The rule is unscoped; a future
        // narrowing that only kept Application/Web would pass every other test
        // here — this case is the canary for that regression.
        var source = InterfaceStubs + """

            namespace Humans.Infrastructure.Jobs
            {
                public class SomeBackgroundJob
                {
                    public async System.Threading.Tasks.Task Run(
                        Humans.Users.Data.Repositories.IUserRepository repo)
                    {
                        await repo.ApplyUserEmailReconcilePlanAsync(null, null, null, null);
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

}
