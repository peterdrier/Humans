using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Humans.Testing;

namespace Humans.Analyzers.Tests;

public class ApplicationServiceDbContextInjectionAnalyzerTests
{
    private const string Stubs = """
        namespace Humans.Infrastructure.Data
        {
            public class HumansDbContext { }
        }

        namespace Humans.Application.Interfaces.Repositories
        {
            public interface IUserRepository { }
        }
        """;

    private static bool IsHum0009(Microsoft.CodeAnalysis.Diagnostic d) =>
        string.Equals(d.Id, ApplicationServiceDbContextInjectionAnalyzer.DiagnosticId, System.StringComparison.Ordinal);

    [HumansFact]
    public async Task Fires_when_Application_service_injects_HumansDbContext()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Profile
            {
                public sealed class ProfileService
                {
                    public ProfileService(Humans.Infrastructure.Data.HumansDbContext dbContext)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ApplicationServiceDbContextInjectionAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0009(d));
    }

    [HumansFact]
    public async Task Fires_for_exact_Application_Services_namespace()
    {
        var source = Stubs + """

            namespace Humans.Application.Services
            {
                public sealed class RootService
                {
                    public RootService(Humans.Infrastructure.Data.HumansDbContext dbContext)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ApplicationServiceDbContextInjectionAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0009(d));
    }

    [HumansFact]
    public async Task Does_not_fire_when_service_injects_repository_interface()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Profile
            {
                public sealed class ProfileService
                {
                    public ProfileService(Humans.Application.Interfaces.Repositories.IUserRepository repository)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ApplicationServiceDbContextInjectionAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(d => IsHum0009(d)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_outside_Application_Services_namespace()
    {
        var source = Stubs + """

            namespace Humans.Application.Jobs
            {
                public sealed class CleanupJob
                {
                    public CleanupJob(Humans.Infrastructure.Data.HumansDbContext dbContext)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ApplicationServiceDbContextInjectionAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(d => IsHum0009(d)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_outside_Application_assembly()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Profile
            {
                public sealed class ProfileService
                {
                    public ProfileService(Humans.Infrastructure.Data.HumansDbContext dbContext)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ApplicationServiceDbContextInjectionAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Should().BeEmpty();
    }
}
