using AwesomeAssertions;

namespace Humans.Analyzers.Tests;

public class ControllerDbContextRuleTests
{
    private const string Stubs = """
        namespace Microsoft.AspNetCore.Mvc
        {
            public abstract class ControllerBase { }
            public abstract class Controller : ControllerBase { }
        }

        namespace Microsoft.EntityFrameworkCore
        {
            public class DbContext { }
        }

        namespace Humans.Infrastructure.Data
        {
            public class UsersDbContext : Microsoft.EntityFrameworkCore.DbContext { }
            public class SystemSettingsDbContext : Microsoft.EntityFrameworkCore.DbContext { }
            public class QueryStatistics { }
        }

        // Detection is structural, not namespace-pinned: a context relocated by
        // the planned assembly reorganization must still be caught.
        namespace Humans.Persistence.Surveys
        {
            public class SurveysDbContext : Microsoft.EntityFrameworkCore.DbContext { }
        }
        """;

    private static bool IsHum0008(Microsoft.CodeAnalysis.Diagnostic d) =>
        string.Equals(d.Id, "HUM0008", StringComparison.Ordinal);

    [HumansFact]
    public async Task Fires_when_controller_injects_UsersDbContext()
    {
        var source = Stubs + """

            namespace Humans.Web.Controllers
            {
                public sealed class ReportsController : Microsoft.AspNetCore.Mvc.Controller
                {
                    public ReportsController(Humans.Infrastructure.Data.UsersDbContext dbContext)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0008(d));
    }

    [HumansFact]
    public async Task Fires_when_controller_injects_a_section_DbContext()
    {
        var source = Stubs + """

            namespace Humans.Web.Controllers
            {
                public sealed class SettingsController : Microsoft.AspNetCore.Mvc.Controller
                {
                    public SettingsController(Humans.Infrastructure.Data.SystemSettingsDbContext dbContext)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0008(d));
    }

    [HumansFact]
    public async Task Fires_when_controller_injects_a_DbContext_from_another_namespace()
    {
        var source = Stubs + """

            namespace Humans.Web.Controllers
            {
                public sealed class SurveysController : Microsoft.AspNetCore.Mvc.Controller
                {
                    public SurveysController(Humans.Persistence.Surveys.SurveysDbContext dbContext)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0008(d));
    }

    [HumansFact]
    public async Task Does_not_fire_for_a_non_DbContext_type_in_the_Data_namespace()
    {
        var source = Stubs + """

            namespace Humans.Web.Controllers
            {
                public sealed class StatsController : Microsoft.AspNetCore.Mvc.Controller
                {
                    public StatsController(Humans.Infrastructure.Data.QueryStatistics stats)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Where(IsHum0008).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Fires_when_controller_injects_nullable_UsersDbContext()
    {
        var source = Stubs + """

            #nullable enable
            namespace Humans.Web.Controllers
            {
                public sealed class ReportsController : Microsoft.AspNetCore.Mvc.Controller
                {
                    public ReportsController(Humans.Infrastructure.Data.UsersDbContext? dbContext)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0008(d));
    }

    [HumansFact]
    public async Task Fires_when_controller_base_subclass_injects_UsersDbContext()
    {
        var source = Stubs + """

            namespace Humans.Web.Controllers
            {
                public sealed class ApiController : Microsoft.AspNetCore.Mvc.ControllerBase
                {
                    public ApiController(Humans.Infrastructure.Data.UsersDbContext dbContext)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0008(d));
    }

    [HumansFact]
    public async Task Does_not_fire_when_controller_injects_service_instead()
    {
        var source = Stubs + """

            namespace Humans.Base.Interfaces.Admin
            {
                public interface IAdminDatabaseDiagnosticsService { }
            }

            namespace Humans.Web.Controllers
            {
                public sealed class AdminController : Microsoft.AspNetCore.Mvc.Controller
                {
                    public AdminController(Humans.Base.Interfaces.Admin.IAdminDatabaseDiagnosticsService diagnostics)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Where(IsHum0008).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Message_names_the_actual_injected_DbContext_type()
    {
        // Detection is structural (any DbContext-derived type), so the message
        // must name the actual context injected rather than a hardcoded
        // "UsersDbContext" (nobodies-collective/Humans#960).
        var source = Stubs + """

            namespace Humans.Web.Controllers
            {
                public sealed class SettingsController : Microsoft.AspNetCore.Mvc.Controller
                {
                    public SettingsController(Humans.Infrastructure.Data.SystemSettingsDbContext dbContext)
                    {
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Should().ContainSingle(d =>
            IsHum0008(d) && d.GetMessage().Contains("SystemSettingsDbContext", StringComparison.Ordinal));
    }

}
