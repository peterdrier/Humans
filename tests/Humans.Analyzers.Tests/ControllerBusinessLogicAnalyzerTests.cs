using AwesomeAssertions;
using Microsoft.CodeAnalysis;

namespace Humans.Analyzers.Tests;

public class ControllerBusinessLogicAnalyzerTests
{
    // GrandfatheredAttribute (widened to AttributeTargets.Method so HUM0031 can
    // grandfather individual controller methods) lives in
    // Humans.Base.Attributes. Stubs mirror the production shapes.
    private const string Stubs = """
        namespace Microsoft.AspNetCore.Mvc
        {
            public abstract class ControllerBase { }
            public abstract class Controller : ControllerBase { }
        }

        namespace Humans.Base.Attributes
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
    public async Task Analyze_OverStatementThreshold_Fires()
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
    public async Task Analyze_OverComplexityThreshold_Fires()
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
    public async Task Analyze_SwitchExpressionArms_CountTowardComplexity()
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
    public async Task Analyze_PrivateControllerHelper_Fires()
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
    public async Task Analyze_GrandfatheredMethod_Warns()
    {
        var source = Stubs + $$"""

            namespace Humans.Web.Controllers
            {
                public sealed class ReportsController : Microsoft.AspNetCore.Mvc.Controller
                {
                    [Humans.Base.Attributes.Grandfathered(
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
    public async Task Analyze_AtThresholds_IsSilent()
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
    public async Task Analyze_NonControllerClass_IsSilent()
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

}
