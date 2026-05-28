using AwesomeAssertions;
using Microsoft.CodeAnalysis;

namespace Humans.Analyzers.Tests;

public class OrchestratorRepositoryInjectionAnalyzerTests
{
    // IOrchestrator and IApplicationService live in Humans.Application.Interfaces;
    // IRepository in Humans.Application.Interfaces.Repositories; HumansDbContext
    // in Humans.Infrastructure.Data. All four are stubbed so the analyzer can
    // resolve them — production builds reference them transitively.
    private const string Stubs = """
        namespace Humans.Application.Interfaces
        {
            public interface IApplicationService { }
            public interface IOrchestrator { }
        }

        namespace Humans.Application.Interfaces.Repositories
        {
            public interface IRepository { }
            public interface IUserRepository : IRepository { }
        }

        namespace Humans.Infrastructure.Data
        {
            public class HumansDbContext { }
        }

        namespace Microsoft.EntityFrameworkCore
        {
            public interface IDbContextFactory<TContext> where TContext : class { }
        }
        """;

    private static bool IsHum0026(Diagnostic d) =>
        string.Equals(d.Id, OrchestratorRepositoryInjectionAnalyzer.RepositoryInjectionDiagnosticId, StringComparison.Ordinal);

    private static bool IsHum0027(Diagnostic d) =>
        string.Equals(d.Id, OrchestratorRepositoryInjectionAnalyzer.RoleConflictDiagnosticId, StringComparison.Ordinal);

    [HumansFact]
    public async Task HUM0026_fires_when_orchestrator_injects_a_repository()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Demo
            {
                public sealed class DemoOrchestrator : Humans.Application.Interfaces.IOrchestrator
                {
                    public DemoOrchestrator(Humans.Application.Interfaces.Repositories.IUserRepository repo)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new OrchestratorRepositoryInjectionAnalyzer(),
            "Humans.Application",
            source);

        var hits = diagnostics.Where(IsHum0026).ToList();
        hits.Should().ContainSingle();
        hits[0].Severity.Should().Be(DiagnosticSeverity.Error);
    }

    [HumansFact]
    public async Task HUM0026_fires_when_orchestrator_injects_HumansDbContext()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Demo
            {
                public sealed class DemoOrchestrator : Humans.Application.Interfaces.IOrchestrator
                {
                    public DemoOrchestrator(Humans.Infrastructure.Data.HumansDbContext db)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new OrchestratorRepositoryInjectionAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0026).Should().ContainSingle();
    }

    [HumansFact]
    public async Task HUM0026_fires_when_orchestrator_injects_DbContextFactory_of_HumansDbContext()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Demo
            {
                public sealed class DemoOrchestrator : Humans.Application.Interfaces.IOrchestrator
                {
                    public DemoOrchestrator(Microsoft.EntityFrameworkCore.IDbContextFactory<Humans.Infrastructure.Data.HumansDbContext> factory)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new OrchestratorRepositoryInjectionAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0026).Should().ContainSingle();
    }

    [HumansFact]
    public async Task HUM0026_fires_when_orchestrator_injects_bare_IRepository_marker()
    {
        // Roslyn's AllInterfaces excludes the interface symbol itself, so a
        // parameter typed as the bare IRepository marker must be detected via
        // equality, not interface-list scan.
        var source = Stubs + """

            namespace Humans.Application.Services.Demo
            {
                public sealed class DemoOrchestrator : Humans.Application.Interfaces.IOrchestrator
                {
                    public DemoOrchestrator(Humans.Application.Interfaces.Repositories.IRepository repo)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new OrchestratorRepositoryInjectionAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0026).Should().ContainSingle();
    }

    [HumansFact]
    public async Task HUM0026_fires_when_orchestrator_injects_repository_via_service_interface()
    {
        // Production pattern: class implements IOrchestrator transitively via
        // its service interface, not directly. Guards against a regression
        // from AllInterfaces (correct) to Interfaces (would silently miss
        // every real orchestrator).
        var source = Stubs + """

            namespace Humans.Application.Services.Demo
            {
                public interface IDemoOrchestrator : Humans.Application.Interfaces.IOrchestrator { }

                public sealed class DemoOrchestratorImpl : IDemoOrchestrator
                {
                    public DemoOrchestratorImpl(Humans.Application.Interfaces.Repositories.IUserRepository repo)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new OrchestratorRepositoryInjectionAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0026).Should().ContainSingle();
    }

    [HumansFact]
    public async Task HUM0027_fires_when_both_markers_held_via_separate_interfaces()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Demo
            {
                public interface IDemoOrchestrator : Humans.Application.Interfaces.IOrchestrator { }
                public interface IDemoSection : Humans.Application.Interfaces.IApplicationService { }

                public sealed class DualMarkerService : IDemoOrchestrator, IDemoSection { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new OrchestratorRepositoryInjectionAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0027).Should().ContainSingle();
    }

    [HumansFact]
    public async Task HUM0026_does_not_fire_on_pure_service_injection()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Demo
            {
                public interface ISomeService { }

                public sealed class DemoOrchestrator : Humans.Application.Interfaces.IOrchestrator
                {
                    public DemoOrchestrator(ISomeService inner)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new OrchestratorRepositoryInjectionAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0026).Should().BeEmpty();
    }

    [HumansFact]
    public async Task HUM0026_does_not_fire_on_non_orchestrator_section_service()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Demo
            {
                public sealed class SectionService : Humans.Application.Interfaces.IApplicationService
                {
                    public SectionService(Humans.Application.Interfaces.Repositories.IUserRepository repo)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new OrchestratorRepositoryInjectionAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0026).Should().BeEmpty();
    }

    [HumansFact]
    public async Task HUM0027_fires_when_type_implements_both_markers()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Demo
            {
                public sealed class HybridService
                    : Humans.Application.Interfaces.IApplicationService,
                      Humans.Application.Interfaces.IOrchestrator
                {
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new OrchestratorRepositoryInjectionAnalyzer(),
            "Humans.Application",
            source);

        var hits = diagnostics.Where(IsHum0027).ToList();
        hits.Should().ContainSingle();
        hits[0].Severity.Should().Be(DiagnosticSeverity.Error);
    }

    [HumansFact]
    public async Task HUM0027_does_not_fire_when_type_implements_only_IOrchestrator()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Demo
            {
                public sealed class PureOrchestrator : Humans.Application.Interfaces.IOrchestrator
                {
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new OrchestratorRepositoryInjectionAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0027).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Analyzer_does_not_fire_outside_Application_assembly()
    {
        var source = Stubs + """

            namespace Humans.Web.Demo
            {
                public sealed class DemoOrchestrator : Humans.Application.Interfaces.IOrchestrator
                {
                    public DemoOrchestrator(Humans.Application.Interfaces.Repositories.IUserRepository repo)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new OrchestratorRepositoryInjectionAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Where(d => IsHum0026(d) || IsHum0027(d)).Should().BeEmpty();
    }
}
