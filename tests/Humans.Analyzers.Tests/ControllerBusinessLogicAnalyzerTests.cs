using AwesomeAssertions;
using Microsoft.CodeAnalysis;

namespace Humans.Analyzers.Tests;

public class ControllerBusinessLogicAnalyzerTests
{
    // GrandfatheredAttribute (widened to AttributeTargets.Method so HUM0031 can
    // grandfather individual controller methods) lives in
    // Humans.Application.Architecture. Stubs mirror the production shapes.
    private const string Stubs = """
        namespace Microsoft.AspNetCore.Mvc
        {
            public abstract class ControllerBase { }
            public abstract class Controller : ControllerBase { }
        }

        namespace Humans.Application.Architecture
        {
            public sealed class GrandfatheredAttribute : System.Attribute
            {
                public GrandfatheredAttribute(string ruleId, string justification, string since, string issueRef) { }
            }
        }
        """;

    private static bool IsHum0031(Diagnostic d) =>
        string.Equals(d.Id, ControllerBusinessLogicAnalyzer.DiagnosticId, StringComparison.Ordinal);

    /// <summary>n sequential statements: <c>var v0 = 0; var v1 = 1; …</c></summary>
    private static string Statements(int n) =>
        string.Concat(Enumerable.Range(0, n).Select(i => $"var v{i} = {i};\n"));

    /// <summary>A boolean expression with n logical-or operators (cc = n + 1).</summary>
    private static string OrChain(int n) =>
        "a == -1 " + string.Concat(Enumerable.Range(0, n).Select(i => $"|| a == {i} "));

    [HumansFact]
    public async Task Fires_error_when_method_exceeds_statement_threshold()
    {
        var source = Stubs + $$"""

            namespace Humans.Web.Controllers
            {
                public sealed class ReportsController : Microsoft.AspNetCore.Mvc.Controller
                {
                    public int Index()
                    {
                        {{Statements(ControllerBusinessLogicAnalyzer.MaxStatements + 1)}}
                        return 0;
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ControllerBusinessLogicAnalyzer(),
            "Humans.Web",
            source);

        var diagnostic = diagnostics.Should().ContainSingle(d => IsHum0031(d)).Subject;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Error);
    }

    [HumansFact]
    public async Task Fires_error_when_method_exceeds_complexity_threshold()
    {
        var source = Stubs + $$"""

            namespace Humans.Web.Controllers
            {
                public sealed class ReportsController : Microsoft.AspNetCore.Mvc.Controller
                {
                    public bool Check(int a)
                    {
                        return {{OrChain(ControllerBusinessLogicAnalyzer.MaxCyclomaticComplexity)}};
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ControllerBusinessLogicAnalyzer(),
            "Humans.Web",
            source);

        var diagnostic = diagnostics.Should().ContainSingle(d => IsHum0031(d)).Subject;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Error);
    }

    [HumansFact]
    public async Task Counts_switch_expression_arms_toward_complexity()
    {
        var arms = string.Concat(Enumerable.Range(0, ControllerBusinessLogicAnalyzer.MaxCyclomaticComplexity)
            .Select(i => $"{i} => \"{i}\",\n"));
        var source = Stubs + $$"""

            namespace Humans.Web.Controllers
            {
                public sealed class ReportsController : Microsoft.AspNetCore.Mvc.Controller
                {
                    public string Label(int a)
                    {
                        return a switch
                        {
                            {{arms}}
                            _ => "other",
                        };
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ControllerBusinessLogicAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0031(d));
    }

    [HumansFact]
    public async Task Fires_on_private_helper_method_in_controller()
    {
        var source = Stubs + $$"""

            namespace Humans.Web.Controllers
            {
                public sealed class ReportsController : Microsoft.AspNetCore.Mvc.Controller
                {
                    private static int BuildModel()
                    {
                        {{Statements(ControllerBusinessLogicAnalyzer.MaxStatements + 1)}}
                        return 0;
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ControllerBusinessLogicAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0031(d));
    }

    [HumansFact]
    public async Task Downgrades_to_warning_when_method_is_grandfathered()
    {
        var source = Stubs + $$"""

            namespace Humans.Web.Controllers
            {
                public sealed class ReportsController : Microsoft.AspNetCore.Mvc.Controller
                {
                    [Humans.Application.Architecture.Grandfathered(
                        "HUM0031", "Pre-existing offender.", "2026-06-09", "nobodies-collective/Humans#793")]
                    public int Index()
                    {
                        {{Statements(ControllerBusinessLogicAnalyzer.MaxStatements + 1)}}
                        return 0;
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ControllerBusinessLogicAnalyzer(),
            "Humans.Web",
            source);

        var diagnostic = diagnostics.Should().ContainSingle(d => IsHum0031(d)).Subject;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
    }

    [HumansFact]
    public async Task Does_not_fire_at_exactly_the_thresholds()
    {
        var source = Stubs + $$"""

            namespace Humans.Web.Controllers
            {
                public sealed class ReportsController : Microsoft.AspNetCore.Mvc.Controller
                {
                    public int AtStatementLimit()
                    {
                        {{Statements(ControllerBusinessLogicAnalyzer.MaxStatements - 1)}}
                        return 0;
                    }

                    public bool AtComplexityLimit(int a)
                    {
                        return {{OrChain(ControllerBusinessLogicAnalyzer.MaxCyclomaticComplexity - 1)}};
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ControllerBusinessLogicAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Where(IsHum0031).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_on_non_controller_class()
    {
        var source = Stubs + $$"""

            namespace Humans.Web.Services
            {
                public sealed class ViewModelBuilder
                {
                    public int Build()
                    {
                        {{Statements(ControllerBusinessLogicAnalyzer.MaxStatements + 1)}}
                        return 0;
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ControllerBusinessLogicAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Where(IsHum0031).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_outside_Web_assembly()
    {
        var source = Stubs + $$"""

            namespace Humans.Application.Services
            {
                public sealed class BigController : Microsoft.AspNetCore.Mvc.Controller
                {
                    public int Index()
                    {
                        {{Statements(ControllerBusinessLogicAnalyzer.MaxStatements + 1)}}
                        return 0;
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ControllerBusinessLogicAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0031).Should().BeEmpty();
    }
}
