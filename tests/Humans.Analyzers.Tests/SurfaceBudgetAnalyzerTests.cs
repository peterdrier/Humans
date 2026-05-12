using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Humans.Testing;

namespace Humans.Analyzers.Tests;

public class SurfaceBudgetAnalyzerTests
{
    /// <summary>
    /// Synthetic stand-in for
    /// <c>Humans.Application.Architecture.SurfaceBudgetAttribute</c>. The
    /// analyzer resolves the attribute by full metadata name, so a stub with
    /// the matching namespace + name is sufficient for tests.
    /// </summary>
    private const string AttributeStub = """
        namespace Humans.Application.Architecture
        {
            [System.AttributeUsage(
                System.AttributeTargets.Interface | System.AttributeTargets.Class | System.AttributeTargets.Struct)]
            public sealed class SurfaceBudgetAttribute : System.Attribute
            {
                public SurfaceBudgetAttribute(int methodCount) => MethodCount = methodCount;
                public int MethodCount { get; }
            }
        }
        """;

    private static bool IsHum0015(Microsoft.CodeAnalysis.Diagnostic d) =>
        string.Equals(d.Id, SurfaceBudgetAnalyzer.OverBudgetDiagnosticId, System.StringComparison.Ordinal);

    private static bool IsHum0016(Microsoft.CodeAnalysis.Diagnostic d) =>
        string.Equals(d.Id, SurfaceBudgetAnalyzer.SlackDiagnosticId, System.StringComparison.Ordinal);

    [HumansFact]
    public async Task At_budget_interface_emits_no_diagnostic()
    {
        var source = AttributeStub + """

            namespace Sample
            {
                using Humans.Application.Architecture;

                [SurfaceBudget(2)]
                public interface ISample
                {
                    void M1();
                    void M2();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SurfaceBudgetAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(d => IsHum0015(d) || IsHum0016(d)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Over_budget_interface_emits_HUM0015()
    {
        var source = AttributeStub + """

            namespace Sample
            {
                using Humans.Application.Architecture;

                [SurfaceBudget(2)]
                public interface ISample
                {
                    void M1();
                    void M2();
                    void M3();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SurfaceBudgetAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0015(d));
        diagnostics.Where(d => IsHum0016(d)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Under_budget_interface_emits_HUM0016()
    {
        var source = AttributeStub + """

            namespace Sample
            {
                using Humans.Application.Architecture;

                [SurfaceBudget(5)]
                public interface ISample
                {
                    void M1();
                    void M2();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SurfaceBudgetAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0016(d));
        diagnostics.Where(d => IsHum0015(d)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Property_accessors_are_not_counted()
    {
        // Budget = 1, plus a property (2 accessors) — should be at-budget.
        var source = AttributeStub + """

            namespace Sample
            {
                using Humans.Application.Architecture;

                [SurfaceBudget(1)]
                public interface ISample
                {
                    string Name { get; set; }
                    void M1();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SurfaceBudgetAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(d => IsHum0015(d) || IsHum0016(d)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Indexers_are_not_counted()
    {
        var source = AttributeStub + """

            namespace Sample
            {
                using Humans.Application.Architecture;

                [SurfaceBudget(1)]
                public interface ISample
                {
                    string this[int i] { get; }
                    void M1();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SurfaceBudgetAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(d => IsHum0015(d) || IsHum0016(d)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Events_are_not_counted()
    {
        var source = AttributeStub + """

            namespace Sample
            {
                using Humans.Application.Architecture;

                [SurfaceBudget(1)]
                public interface ISample
                {
                    event System.EventHandler Changed;
                    void M1();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SurfaceBudgetAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(d => IsHum0015(d) || IsHum0016(d)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Inherited_methods_are_not_counted()
    {
        // Base interface contributes 3 methods, but the budgeted interface
        // declares only 2 of its own — budget=2 should be at-budget.
        var source = AttributeStub + """

            namespace Sample
            {
                using Humans.Application.Architecture;

                public interface IBase
                {
                    void B1();
                    void B2();
                    void B3();
                }

                [SurfaceBudget(2)]
                public interface ISample : IBase
                {
                    void M1();
                    void M2();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SurfaceBudgetAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(d => IsHum0015(d) || IsHum0016(d)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Undecorated_interface_emits_no_diagnostic()
    {
        var source = AttributeStub + """

            namespace Sample
            {
                public interface IUndecorated
                {
                    void M1();
                    void M2();
                    void M3();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SurfaceBudgetAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(d => IsHum0015(d) || IsHum0016(d)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Class_over_budget_emits_HUM0015()
    {
        var source = AttributeStub + """

            namespace Sample
            {
                using Humans.Application.Architecture;

                [SurfaceBudget(2)]
                public class Sample
                {
                    public void M1() { }
                    public void M2() { }
                    public void M3() { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SurfaceBudgetAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0015(d));
        diagnostics.Where(d => IsHum0016(d)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Class_private_and_static_methods_are_not_counted()
    {
        // Budget = 1 public-instance method. Private/internal/protected/static
        // methods must not contribute to the count.
        var source = AttributeStub + """

            namespace Sample
            {
                using Humans.Application.Architecture;

                [SurfaceBudget(1)]
                public class Sample
                {
                    public void Public1() { }
                    private void Private1() { }
                    internal void Internal1() { }
                    protected void Protected1() { }
                    public static void Static1() { }
                    private static void PrivateStatic1() { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SurfaceBudgetAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(d => IsHum0015(d) || IsHum0016(d)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Struct_over_budget_emits_HUM0015()
    {
        var source = AttributeStub + """

            namespace Sample
            {
                using Humans.Application.Architecture;

                [SurfaceBudget(1)]
                public struct Sample
                {
                    public void M1() { }
                    public void M2() { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SurfaceBudgetAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0015(d));
        diagnostics.Where(d => IsHum0016(d)).Should().BeEmpty();
    }
}
