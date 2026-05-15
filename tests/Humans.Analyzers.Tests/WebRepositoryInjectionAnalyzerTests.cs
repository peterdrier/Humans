using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Humans.Testing;

namespace Humans.Analyzers.Tests;

public class WebRepositoryInjectionAnalyzerTests
{
    private const string Stubs = """
        namespace Humans.Application.Interfaces.Repositories
        {
            public interface IRepository { }
            public interface ICampRepository : IRepository { }
        }

        namespace Humans.Application.Interfaces.Camps
        {
            public interface ICampService { }
        }
        """;

    private static bool IsHum0014(Microsoft.CodeAnalysis.Diagnostic d) =>
        string.Equals(d.Id, WebRepositoryInjectionAnalyzer.DiagnosticId, System.StringComparison.Ordinal);

    [HumansFact]
    public async Task Fires_when_controller_injects_repository()
    {
        var source = Stubs + """

            namespace Humans.Web.Controllers
            {
                public sealed class CampsController
                {
                    public CampsController(Humans.Application.Interfaces.Repositories.ICampRepository repo) { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new WebRepositoryInjectionAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0014(d));
    }

    [HumansFact]
    public async Task Fires_when_view_component_injects_repository()
    {
        var source = Stubs + """

            namespace Humans.Web.ViewComponents
            {
                public sealed class CampSummaryViewComponent
                {
                    public CampSummaryViewComponent(Humans.Application.Interfaces.Repositories.ICampRepository repo) { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new WebRepositoryInjectionAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0014(d));
    }

    [HumansFact]
    public async Task Does_not_fire_when_controller_injects_service()
    {
        var source = Stubs + """

            namespace Humans.Web.Controllers
            {
                public sealed class CampsController
                {
                    public CampsController(Humans.Application.Interfaces.Camps.ICampService service) { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new WebRepositoryInjectionAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Where(d => IsHum0014(d)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_outside_Web_assembly()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Repositories.Camps
            {
                public sealed class CachingCampRepository
                {
                    public CachingCampRepository(Humans.Application.Interfaces.Repositories.ICampRepository inner) { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new WebRepositoryInjectionAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Where(d => IsHum0014(d)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Fires_on_indirect_repository_extension()
    {
        var source = """
            namespace Humans.Application.Interfaces.Repositories
            {
                public interface IRepository { }
                public interface IMid : IRepository { }
                public interface IDeepRepository : IMid { }
            }

            namespace Humans.Web.Controllers
            {
                public sealed class DeepController
                {
                    public DeepController(Humans.Application.Interfaces.Repositories.IDeepRepository repo) { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new WebRepositoryInjectionAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0014(d));
    }
}
