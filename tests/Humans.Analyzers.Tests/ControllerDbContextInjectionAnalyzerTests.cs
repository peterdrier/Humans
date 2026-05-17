using AwesomeAssertions;

namespace Humans.Analyzers.Tests;

public class ControllerDbContextInjectionAnalyzerTests
{
    private const string Stubs = """
        namespace Microsoft.AspNetCore.Mvc
        {
            public abstract class ControllerBase { }
            public abstract class Controller : ControllerBase { }
        }

        namespace Humans.Infrastructure.Data
        {
            public class HumansDbContext { }
        }
        """;

    private static bool IsHum0008(Microsoft.CodeAnalysis.Diagnostic d) =>
        string.Equals(d.Id, ControllerDbContextInjectionAnalyzer.DiagnosticId, StringComparison.Ordinal);

    [HumansFact]
    public async Task Fires_when_controller_injects_HumansDbContext()
    {
        var source = Stubs + """

            namespace Humans.Web.Controllers
            {
                public sealed class ReportsController : Microsoft.AspNetCore.Mvc.Controller
                {
                    public ReportsController(Humans.Infrastructure.Data.HumansDbContext dbContext)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ControllerDbContextInjectionAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0008(d));
    }

    [HumansFact]
    public async Task Fires_when_controller_injects_nullable_HumansDbContext()
    {
        var source = Stubs + """

            #nullable enable
            namespace Humans.Web.Controllers
            {
                public sealed class ReportsController : Microsoft.AspNetCore.Mvc.Controller
                {
                    public ReportsController(Humans.Infrastructure.Data.HumansDbContext? dbContext)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ControllerDbContextInjectionAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0008(d));
    }

    [HumansFact]
    public async Task Fires_when_controller_base_subclass_injects_HumansDbContext()
    {
        var source = Stubs + """

            namespace Humans.Web.Controllers
            {
                public sealed class ApiController : Microsoft.AspNetCore.Mvc.ControllerBase
                {
                    public ApiController(Humans.Infrastructure.Data.HumansDbContext dbContext)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ControllerDbContextInjectionAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0008(d));
    }

    [HumansFact]
    public async Task Does_not_fire_when_controller_injects_service_instead()
    {
        var source = Stubs + """

            namespace Humans.Application.Interfaces.Admin
            {
                public interface IAdminDatabaseDiagnosticsService { }
            }

            namespace Humans.Web.Controllers
            {
                public sealed class AdminController : Microsoft.AspNetCore.Mvc.Controller
                {
                    public AdminController(Humans.Application.Interfaces.Admin.IAdminDatabaseDiagnosticsService diagnostics)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ControllerDbContextInjectionAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Where(IsHum0008).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_outside_Web_assembly()
    {
        var source = Stubs + """

            namespace Humans.Web.Controllers
            {
                public sealed class ReportsController : Microsoft.AspNetCore.Mvc.Controller
                {
                    public ReportsController(Humans.Infrastructure.Data.HumansDbContext dbContext)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ControllerDbContextInjectionAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().BeEmpty();
    }
}
