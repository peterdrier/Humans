using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Humans.Testing;

namespace Humans.Analyzers.Tests;

public class RepositoryInterfaceLocationAnalyzerTests
{
    private const string Stubs = """
        namespace Humans.Application.Interfaces.Repositories
        {
            public interface IRepository { }
        }
        """;

    private static bool IsHum0013(Microsoft.CodeAnalysis.Diagnostic d) =>
        string.Equals(d.Id, RepositoryInterfaceLocationAnalyzer.DiagnosticId, System.StringComparison.Ordinal);

    [HumansFact]
    public async Task Fires_when_repository_interface_lives_outside_Repositories_namespace()
    {
        var source = Stubs + """

            namespace Humans.Application.Interfaces.Camps
            {
                public interface ICampRepository : Humans.Application.Interfaces.Repositories.IRepository { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new RepositoryInterfaceLocationAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0013(d));
    }

    [HumansFact]
    public async Task Does_not_fire_when_repository_interface_is_in_Repositories_namespace()
    {
        var source = Stubs + """

            namespace Humans.Application.Interfaces.Repositories
            {
                public interface ICampRepository : Humans.Application.Interfaces.Repositories.IRepository { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new RepositoryInterfaceLocationAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(d => IsHum0013(d)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_on_the_IRepository_marker_itself()
    {
        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new RepositoryInterfaceLocationAnalyzer(),
            "Humans.Application",
            Stubs);

        diagnostics.Where(d => IsHum0013(d)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_on_non_repository_interface()
    {
        var source = Stubs + """

            namespace Humans.Application.Interfaces.Camps
            {
                public interface ICampService { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new RepositoryInterfaceLocationAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(d => IsHum0013(d)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_on_repository_implementation_class()
    {
        var source = Stubs + """

            namespace Humans.Application.Interfaces.Repositories
            {
                public interface ICampRepository : Humans.Application.Interfaces.Repositories.IRepository { }
            }

            namespace Humans.Infrastructure.Repositories.Camps
            {
                public sealed class CampRepository : Humans.Application.Interfaces.Repositories.ICampRepository { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new RepositoryInterfaceLocationAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(d => IsHum0013(d)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Fires_when_repository_extends_IRepository_indirectly()
    {
        var source = """
            namespace Humans.Application.Interfaces.Repositories
            {
                public interface IRepository { }
            }

            namespace Humans.Application.Interfaces.Mid
            {
                public interface IMidRepository : Humans.Application.Interfaces.Repositories.IRepository { }
            }

            namespace Humans.Application.Interfaces.Camps
            {
                public interface ICampRepository : Humans.Application.Interfaces.Mid.IMidRepository { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new RepositoryInterfaceLocationAnalyzer(),
            "Humans.Application",
            source);

        // Both IMidRepository and ICampRepository extend IRepository transitively
        // and live outside the expected namespace.
        diagnostics.Where(d => IsHum0013(d)).Should().HaveCount(2);
    }

    [HumansFact]
    public async Task Grandfathered_violator_downgrades_to_warning()
    {
        var source = Stubs + """

            namespace Humans.Application.Architecture
            {
                [System.AttributeUsage(System.AttributeTargets.Interface, AllowMultiple = true)]
                public sealed class GrandfatheredAttribute : System.Attribute
                {
                    public GrandfatheredAttribute(string ruleId, string justification, string since, string issueRef) { }
                }
            }

            namespace Humans.Application.Interfaces.Camps
            {
                [Humans.Application.Architecture.Grandfathered("HUM0013", "test", "2026-05-15", "test")]
                public interface ICampRepository : Humans.Application.Interfaces.Repositories.IRepository { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new RepositoryInterfaceLocationAnalyzer(),
            "Humans.Application",
            source);

        var hum0013 = diagnostics.Where(d => IsHum0013(d)).ToList();
        hum0013.Should().ContainSingle();
        hum0013[0].Severity.Should().Be(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);
    }

    [HumansFact]
    public async Task Does_not_fire_outside_Application_assembly()
    {
        var source = Stubs + """

            namespace Humans.Application.Interfaces.Camps
            {
                public interface ICampRepository : Humans.Application.Interfaces.Repositories.IRepository { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new RepositoryInterfaceLocationAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Should().BeEmpty();
    }
}
