using AwesomeAssertions;
using Microsoft.CodeAnalysis;

namespace Humans.Analyzers.Tests;

public class DbContextOwnershipRuleTests
{
    // The analyzer keys off the full names of UsersDbContext, the IRepository marker and
    // GrandfatheredAttribute; all are stubbed so the synthetic compilation can resolve them.
    private const string Stubs = """
        namespace Microsoft.EntityFrameworkCore
        {
            public class DbContext { }
        }

        namespace Humans.Infrastructure.Data
        {
            public class UsersDbContext : Microsoft.EntityFrameworkCore.DbContext { }
            public class SystemSettingsDbContext : Microsoft.EntityFrameworkCore.DbContext { }
        }

        namespace Humans.Base.Interfaces.Repositories
        {
            public interface IRepository { }
            public interface IUserRepository : IRepository { }
        }

        namespace Humans.Base.Attributes
        {
            [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
            public sealed class GrandfatheredAttribute : System.Attribute
            {
                public GrandfatheredAttribute(string ruleId, string justification, string since, string issueRef) { }
            }
        }

        namespace Microsoft.EntityFrameworkCore.Design
        {
            public interface IDesignTimeDbContextFactory<TContext>
            {
                TContext CreateDbContext(string[] args);
            }
        }

        namespace Microsoft.Extensions.Hosting
        {
            public interface IHostedLifecycleService { }
        }
        """;

    private static bool IsHum0009(Diagnostic d) =>
        string.Equals(d.Id, "HUM0009", StringComparison.Ordinal);

    [HumansFact]
    public async Task Analyze_NonRepositoryUsersDbContext_Fires()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Jobs
            {
                public sealed class SomeJob
                {
                    public SomeJob(Humans.Infrastructure.Data.UsersDbContext dbContext)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Infrastructure",
            source);

        var hits = diagnostics.Where(IsHum0009).ToList();
        hits.Should().ContainSingle();
        hits[0].Severity.Should().Be(DiagnosticSeverity.Error);
    }

    [HumansFact]
    public async Task Analyze_NonRepositorySectionDbContext_Fires()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Jobs
            {
                public sealed class SomeJob
                {
                    public SomeJob(Humans.Infrastructure.Data.SystemSettingsDbContext dbContext)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Infrastructure",
            source);

        var hits = diagnostics.Where(IsHum0009).ToList();
        hits.Should().ContainSingle();
        hits[0].Severity.Should().Be(DiagnosticSeverity.Error);
    }

    [HumansFact]
    public async Task Analyze_Repository_IsSilent()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Repositories.Users
            {
                public sealed class UserRepository : Humans.Base.Interfaces.Repositories.IUserRepository
                {
                    public UserRepository(Humans.Infrastructure.Data.UsersDbContext dbContext)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Where(IsHum0009).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Analyze_DesignTimeFactory_IsSilent()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Data
            {
                public sealed class UsersDbContextFactory :
                    Microsoft.EntityFrameworkCore.Design.IDesignTimeDbContextFactory<Humans.Infrastructure.Data.UsersDbContext>
                {
                    public Humans.Infrastructure.Data.UsersDbContext CreateDbContext(string[] args) => new();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Where(IsHum0009).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Analyze_HostingLifecycleService_IsSilent()
    {
        var source = Stubs + """

            namespace Humans.Base.Hosting
            {
                public sealed class DatabaseMigrationHostedService :
                    Microsoft.Extensions.Hosting.IHostedLifecycleService
                {
                    public void Run(Humans.Infrastructure.Data.UsersDbContext dbContext) { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Where(IsHum0009).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Analyze_LifecycleServiceOutsideHosting_Fires()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Jobs
            {
                public sealed class SomeHostedJob :
                    Microsoft.Extensions.Hosting.IHostedLifecycleService
                {
                    public void Run(Humans.Infrastructure.Data.UsersDbContext dbContext) { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Where(IsHum0009).Should().ContainSingle();
    }

    [HumansFact]
    public async Task Analyze_FieldReference_Fires()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Jobs
            {
                public sealed class SomeJob
                {
                    private readonly Humans.Infrastructure.Data.UsersDbContext _db = null!;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Where(IsHum0009).Should().ContainSingle();
    }

    [HumansFact]
    public async Task Analyze_MethodParameter_Fires()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Jobs
            {
                public sealed class SomeJob
                {
                    public void Run(Humans.Infrastructure.Data.UsersDbContext db) { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Where(IsHum0009).Should().ContainSingle();
    }

    [HumansFact]
    public async Task Analyze_GrandfatheredClass_Warns()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Jobs
            {
                [Humans.Base.Attributes.Grandfathered(
                    ruleId: "HUM0009",
                    justification: "Pending migration to repository pattern.",
                    since: "2026-05-12",
                    issueRef: "nobodies-collective/Humans#701")]
                public sealed class SomeJob
                {
                    public SomeJob(Humans.Infrastructure.Data.UsersDbContext dbContext)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Infrastructure",
            source);

        var hits = diagnostics.Where(IsHum0009).ToList();
        hits.Should().ContainSingle();
        hits[0].Severity.Should().Be(DiagnosticSeverity.Warning);
    }

    [HumansFact]
    public async Task Analyze_GrandfatheredOtherRule_Fires()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Jobs
            {
                [Humans.Base.Attributes.Grandfathered(
                    ruleId: "HUM0042",
                    justification: "Different rule.",
                    since: "2026-05-12",
                    issueRef: "nobodies-collective/Humans#0")]
                public sealed class SomeJob
                {
                    public SomeJob(Humans.Infrastructure.Data.UsersDbContext dbContext)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Infrastructure",
            source);

        var hits = diagnostics.Where(IsHum0009).ToList();
        hits.Should().ContainSingle();
        hits[0].Severity.Should().Be(DiagnosticSeverity.Error);
    }

    [HumansFact]
    public async Task Analyze_BaseClassTypeArgument_Fires()
    {
        // Pins the recursive type-argument walk in TypeReferences. Without it
        // the analyzer would silently miss UserStore<…, UsersDbContext, …>
        // — the LoggingUserStoreDecorator case that motivated the walk.
        var source = Stubs + """

            namespace System.Identity
            {
                public class UserStore<TUser, TContext> where TContext : class { }
            }

            namespace Humans.Infrastructure.Identity
            {
                public sealed class LoggingUserStoreDecorator
                    : System.Identity.UserStore<object, Humans.Infrastructure.Data.UsersDbContext>
                {
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Infrastructure",
            source);

        var hits = diagnostics.Where(IsHum0009).ToList();
        hits.Should().ContainSingle();
        hits[0].Severity.Should().Be(DiagnosticSeverity.Error);
    }

    [HumansFact]
    public async Task Message_UsedDbContext_NamesItsType()
    {
        // Detection is structural (any DbContext-derived type), so the message
        // must name the actual context used rather than a hardcoded
        // "UsersDbContext".
        var source = Stubs + """

            namespace Humans.Infrastructure.Jobs
            {
                public sealed class SomeJob
                {
                    public SomeJob(Humans.Infrastructure.Data.SystemSettingsDbContext dbContext)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Should().ContainSingle(d =>
            IsHum0009(d) && d.GetMessage().Contains("SystemSettingsDbContext", StringComparison.Ordinal));
    }

}
