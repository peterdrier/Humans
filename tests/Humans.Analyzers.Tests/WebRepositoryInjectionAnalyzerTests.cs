using AwesomeAssertions;

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

        namespace Microsoft.AspNetCore.Mvc
        {
            public abstract class ControllerBase { }
        }
        """;

    private static bool IsHum0014(Microsoft.CodeAnalysis.Diagnostic d) =>
        string.Equals(d.Id, WebRepositoryInjectionAnalyzer.DiagnosticId, StringComparison.Ordinal);

    [HumansFact]
    public async Task Fires_when_controller_injects_repository()
    {
        var source = Stubs + """

            namespace Humans.Web.Controllers
            {
                public sealed class CampsController : Microsoft.AspNetCore.Mvc.ControllerBase
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
    public async Task Does_not_fire_when_controller_injects_service()
    {
        var source = Stubs + """

            namespace Humans.Web.Controllers
            {
                public sealed class CampsController : Microsoft.AspNetCore.Mvc.ControllerBase
                {
                    public CampsController(Humans.Application.Interfaces.Camps.ICampService service) { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new WebRepositoryInjectionAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Where(IsHum0014).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Grandfathered_violator_downgrades_to_warning()
    {
        var source = Stubs + """

            namespace Humans.Application.Architecture
            {
                [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = true)]
                public sealed class GrandfatheredAttribute : System.Attribute
                {
                    public GrandfatheredAttribute(string ruleId, string justification, string since, string issueRef) { }
                }
            }

            namespace Humans.Web.Controllers
            {
                [Humans.Application.Architecture.Grandfathered("HUM0014", "test", "2026-05-15", "test")]
                public sealed class CampsController : Microsoft.AspNetCore.Mvc.ControllerBase
                {
                    public CampsController(Humans.Application.Interfaces.Repositories.ICampRepository repo) { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new WebRepositoryInjectionAnalyzer(),
            "Humans.Web",
            source);

        var hum0014 = diagnostics.Where(IsHum0014).ToList();
        hum0014.Should().ContainSingle();
        hum0014[0].Severity.Should().Be(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);
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

            namespace Microsoft.AspNetCore.Mvc
            {
                public abstract class ControllerBase { }
            }

            namespace Humans.Web.Controllers
            {
                public sealed class DeepController : Microsoft.AspNetCore.Mvc.ControllerBase
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
