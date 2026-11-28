using AwesomeAssertions;
using Microsoft.CodeAnalysis;

namespace Humans.Analyzers.Tests;

public class OrchestratorRepositoryInjectionAnalyzerTests
{
    // IOrchestrator, IApplicationService, IRepository and UsersDbContext are stubbed so the
    // analyzer can resolve them by name.
    private const string Stubs = """
        namespace Humans.Base.Interfaces
        {
            public interface IApplicationService { }
            public interface IOrchestrator { }
        }

        namespace Humans.Base.Interfaces.Repositories
        {
            public interface IRepository { }
            public interface IUserRepository : IRepository { }
        }

        namespace Microsoft.EntityFrameworkCore
        {
            public class DbContext { }
            public interface IDbContextFactory<TContext> where TContext : class { }
        }

        namespace Humans.Infrastructure.Data
        {
            public class UsersDbContext : Microsoft.EntityFrameworkCore.DbContext { }
            public class SystemSettingsDbContext : Microsoft.EntityFrameworkCore.DbContext { }
        }
        """;

    private static bool IsHum0026(Diagnostic d) =>
        string.Equals(d.Id, OrchestratorRepositoryInjectionAnalyzer.RepositoryInjectionDiagnosticId, StringComparison.Ordinal);

    private static bool IsHum0027(Diagnostic d) =>
        string.Equals(d.Id, OrchestratorRepositoryInjectionAnalyzer.RoleConflictDiagnosticId, StringComparison.Ordinal);

    [HumansFact]
    public async Task HUM0026_Repository_Fires()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Demo
            {
                public sealed class DemoOrchestrator : Humans.Base.Interfaces.IOrchestrator
                {
                    public DemoOrchestrator(Humans.Base.Interfaces.Repositories.IUserRepository repo)
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
    public async Task HUM0026_UsersDbContext_Fires()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Demo
            {
                public sealed class DemoOrchestrator : Humans.Base.Interfaces.IOrchestrator
                {
                    public DemoOrchestrator(Humans.Infrastructure.Data.UsersDbContext db)
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
    public async Task HUM0026_SectionDbContext_Fires()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Demo
            {
                public sealed class DemoOrchestrator : Humans.Base.Interfaces.IOrchestrator
                {
                    public DemoOrchestrator(Humans.Infrastructure.Data.SystemSettingsDbContext db)
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
    public async Task HUM0026_DbContextFactory_Fires()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Demo
            {
                public sealed class DemoOrchestrator : Humans.Base.Interfaces.IOrchestrator
                {
                    public DemoOrchestrator(Microsoft.EntityFrameworkCore.IDbContextFactory<Humans.Infrastructure.Data.UsersDbContext> factory)
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
    public async Task HUM0026_BareRepositoryMarker_Fires()
    {
        // Roslyn's AllInterfaces excludes the interface symbol itself, so a
        // parameter typed as the bare IRepository marker must be detected via
        // equality, not interface-list scan.
        var source = Stubs + """

            namespace Humans.Application.Services.Demo
            {
                public sealed class DemoOrchestrator : Humans.Base.Interfaces.IOrchestrator
                {
                    public DemoOrchestrator(Humans.Base.Interfaces.Repositories.IRepository repo)
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
    public async Task HUM0026_RepositoryViaServiceInterface_Fires()
    {
        // Production pattern: class implements IOrchestrator transitively via
        // its service interface, not directly. Guards against a regression
        // from AllInterfaces (correct) to Interfaces (would silently miss
        // every real orchestrator).
        var source = Stubs + """

            namespace Humans.Application.Services.Demo
            {
                public interface IDemoOrchestrator : Humans.Base.Interfaces.IOrchestrator { }

                public sealed class DemoOrchestratorImpl : IDemoOrchestrator
                {
                    public DemoOrchestratorImpl(Humans.Base.Interfaces.Repositories.IUserRepository repo)
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
    public async Task HUM0027_MarkersViaSeparateInterfaces_Fires()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Demo
            {
                public interface IDemoOrchestrator : Humans.Base.Interfaces.IOrchestrator { }
                public interface IDemoSection : Humans.Base.Interfaces.IApplicationService { }

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
    public async Task HUM0026_ServiceInjection_IsSilent()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Demo
            {
                public interface ISomeService { }

                public sealed class DemoOrchestrator : Humans.Base.Interfaces.IOrchestrator
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
    public async Task HUM0026_NonOrchestratorService_IsSilent()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Demo
            {
                public sealed class SectionService : Humans.Base.Interfaces.IApplicationService
                {
                    public SectionService(Humans.Base.Interfaces.Repositories.IUserRepository repo)
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
    public async Task HUM0027_BothMarkers_Fires()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Demo
            {
                public sealed class HybridService
                    : Humans.Base.Interfaces.IApplicationService,
                      Humans.Base.Interfaces.IOrchestrator
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
    public async Task HUM0027_OnlyOrchestratorMarker_IsSilent()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Demo
            {
                public sealed class PureOrchestrator : Humans.Base.Interfaces.IOrchestrator
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

}
