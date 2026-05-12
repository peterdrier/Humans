using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Humans.Testing;
using Microsoft.CodeAnalysis;

namespace Humans.Analyzers.Tests;

public class ApplicationServiceDbContextInjectionAnalyzerTests
{
    // HumansDbContext lives in Humans.Infrastructure; the IRepository marker
    // lives in Humans.Application. The analyzer keys off these full names and
    // off the GrandfatheredAttribute. All four are stubbed in the synthetic
    // compilation so the analyzer can resolve them — the real build
    // references them transitively.
    private const string Stubs = """
        namespace Humans.Infrastructure.Data
        {
            public class HumansDbContext { }
        }

        namespace Humans.Application.Interfaces.Repositories
        {
            public interface IRepository { }
            public interface IUserRepository : IRepository { }
        }

        namespace Humans.Application.Architecture
        {
            [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
            public sealed class GrandfatheredAttribute : System.Attribute
            {
                public GrandfatheredAttribute(string ruleId, string justification, string since, string issueRef) { }
            }
        }
        """;

    private static bool IsHum0009(Diagnostic d) =>
        string.Equals(d.Id, ApplicationServiceDbContextInjectionAnalyzer.DiagnosticId, System.StringComparison.Ordinal);

    [HumansFact]
    public async Task Fires_error_on_non_repository_class_using_HumansDbContext()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Jobs
            {
                public sealed class SomeJob
                {
                    public SomeJob(Humans.Infrastructure.Data.HumansDbContext dbContext)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ApplicationServiceDbContextInjectionAnalyzer(),
            "Humans.Infrastructure",
            source);

        var hits = diagnostics.Where(IsHum0009).ToList();
        hits.Should().ContainSingle();
        hits[0].Severity.Should().Be(DiagnosticSeverity.Error);
    }

    [HumansFact]
    public async Task Does_not_fire_on_repository_implementation()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Repositories.Users
            {
                public sealed class UserRepository : Humans.Application.Interfaces.Repositories.IUserRepository
                {
                    public UserRepository(Humans.Infrastructure.Data.HumansDbContext dbContext)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ApplicationServiceDbContextInjectionAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Where(IsHum0009).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Fires_on_field_reference_not_just_constructor_param()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Jobs
            {
                public sealed class SomeJob
                {
                    private readonly Humans.Infrastructure.Data.HumansDbContext _db = null!;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ApplicationServiceDbContextInjectionAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Where(IsHum0009).Should().ContainSingle();
    }

    [HumansFact]
    public async Task Fires_on_method_parameter()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Jobs
            {
                public sealed class SomeJob
                {
                    public void Run(Humans.Infrastructure.Data.HumansDbContext db) { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ApplicationServiceDbContextInjectionAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Where(IsHum0009).Should().ContainSingle();
    }

    [HumansFact]
    public async Task Downgrades_to_warning_when_class_has_Grandfathered_for_HUM0009()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Jobs
            {
                [Humans.Application.Architecture.Grandfathered(
                    ruleId: "HUM0009",
                    justification: "Pending migration to repository pattern.",
                    since: "2026-05-12",
                    issueRef: "nobodies-collective/Humans#701")]
                public sealed class SomeJob
                {
                    public SomeJob(Humans.Infrastructure.Data.HumansDbContext dbContext)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ApplicationServiceDbContextInjectionAnalyzer(),
            "Humans.Infrastructure",
            source);

        var hits = diagnostics.Where(IsHum0009).ToList();
        hits.Should().ContainSingle();
        hits[0].Severity.Should().Be(DiagnosticSeverity.Warning);
    }

    [HumansFact]
    public async Task Grandfathered_for_a_different_rule_still_fires_error()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Jobs
            {
                [Humans.Application.Architecture.Grandfathered(
                    ruleId: "HUM0042",
                    justification: "Different rule.",
                    since: "2026-05-12",
                    issueRef: "nobodies-collective/Humans#0")]
                public sealed class SomeJob
                {
                    public SomeJob(Humans.Infrastructure.Data.HumansDbContext dbContext)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ApplicationServiceDbContextInjectionAnalyzer(),
            "Humans.Infrastructure",
            source);

        var hits = diagnostics.Where(IsHum0009).ToList();
        hits.Should().ContainSingle();
        hits[0].Severity.Should().Be(DiagnosticSeverity.Error);
    }

    [HumansFact]
    public async Task Fires_on_HumansDbContext_as_generic_type_argument_of_base_class()
    {
        // Pins the recursive type-argument walk in TypeReferences. Without it
        // the analyzer would silently miss UserStore<…, HumansDbContext, …>
        // — the LoggingUserStoreDecorator case that motivated the walk.
        var source = Stubs + """

            namespace System.Identity
            {
                public class UserStore<TUser, TContext> where TContext : class { }
            }

            namespace Humans.Infrastructure.Identity
            {
                public sealed class LoggingUserStoreDecorator
                    : System.Identity.UserStore<object, Humans.Infrastructure.Data.HumansDbContext>
                {
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ApplicationServiceDbContextInjectionAnalyzer(),
            "Humans.Infrastructure",
            source);

        var hits = diagnostics.Where(IsHum0009).ToList();
        hits.Should().ContainSingle();
        hits[0].Severity.Should().Be(DiagnosticSeverity.Error);
    }

    [HumansFact]
    public async Task Does_not_fire_outside_Infrastructure_assembly()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Jobs
            {
                public sealed class SomeJob
                {
                    public SomeJob(Humans.Infrastructure.Data.HumansDbContext dbContext)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ApplicationServiceDbContextInjectionAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0009).Should().BeEmpty();
    }
}
