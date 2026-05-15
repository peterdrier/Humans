using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Humans.Testing;

namespace Humans.Analyzers.Tests;

public class ApplicationServiceLocationAnalyzerTests
{
    private const string Stubs = """
        namespace Humans.Application.Interfaces
        {
            public interface IApplicationService { }
        }

        namespace Humans.Application.Interfaces.Camps
        {
            public interface ICampService : Humans.Application.Interfaces.IApplicationService { }
        }
        """;

    private static bool IsHum0012(Microsoft.CodeAnalysis.Diagnostic d) =>
        string.Equals(d.Id, ApplicationServiceLocationAnalyzer.DiagnosticId, System.StringComparison.Ordinal);

    [HumansFact]
    public async Task Fires_when_service_lives_outside_Services_namespace()
    {
        var source = Stubs + """

            namespace Humans.Application.Camps
            {
                public sealed class CampService : Humans.Application.Interfaces.Camps.ICampService { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ApplicationServiceLocationAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0012(d));
    }

    [HumansFact]
    public async Task Does_not_fire_when_service_lives_under_Services_section()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Camps
            {
                public sealed class CampService : Humans.Application.Interfaces.Camps.ICampService { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ApplicationServiceLocationAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(d => IsHum0012(d)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_on_non_service_class()
    {
        var source = """
            namespace Humans.Application.Interfaces
            {
                public interface IApplicationService { }
            }

            namespace Humans.Application.Some.Other.Place
            {
                public sealed class JustAHelper { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ApplicationServiceLocationAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(d => IsHum0012(d)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_on_abstract_service_base_class()
    {
        var source = Stubs + """

            namespace Humans.Application.Internal
            {
                public abstract class CampServiceBase : Humans.Application.Interfaces.Camps.ICampService { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ApplicationServiceLocationAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(d => IsHum0012(d)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Fires_when_service_implements_marker_indirectly_through_chain()
    {
        var source = """
            namespace Humans.Application.Interfaces
            {
                public interface IApplicationService { }
            }

            namespace Humans.Application.Interfaces.Mid
            {
                public interface IMidService : Humans.Application.Interfaces.IApplicationService { }
            }

            namespace Humans.Application.Interfaces.Deep
            {
                public interface IDeepService : Humans.Application.Interfaces.Mid.IMidService { }
            }

            namespace Humans.Application.Wrong
            {
                public sealed class DeepService : Humans.Application.Interfaces.Deep.IDeepService { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ApplicationServiceLocationAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0012(d));
    }

    [HumansFact]
    public async Task Does_not_fire_outside_Application_assembly()
    {
        var source = Stubs + """

            namespace Humans.Application.Camps
            {
                public sealed class CampService : Humans.Application.Interfaces.Camps.ICampService { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ApplicationServiceLocationAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Should().BeEmpty();
    }
}
