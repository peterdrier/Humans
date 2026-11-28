using AwesomeAssertions;
using Microsoft.CodeAnalysis;

namespace Humans.Analyzers.Tests;

public class InvalidatorInterfaceRatchetAnalyzerTests
{
    // IInvalidator lives in Humans.Base.Interfaces; GrandfatheredAttribute
    // (widened to AttributeTargets.Class | Interface so HUM0028 can grandfather
    // interface declarations) in Humans.Base.Attributes. Stubs mirror
    // the production shapes so the analyzer can resolve them.
    private const string Stubs = """
        namespace Humans.Base.Interfaces
        {
            public interface IInvalidator { }
        }

        namespace Humans.Base.Attributes
        {
            [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Interface, AllowMultiple = true, Inherited = false)]
            public sealed class GrandfatheredAttribute : System.Attribute
            {
                public GrandfatheredAttribute(string ruleId, string justification, string since, string issueRef) { }
            }
        }
        """;

    private static bool IsHum0028(Diagnostic d) =>
        string.Equals(d.Id, InvalidatorInterfaceRatchetAnalyzer.DiagnosticId, StringComparison.Ordinal);

    [HumansFact]
    public async Task Analyze_NewInvalidatorInterface_Fires()
    {
        var source = Stubs + """

            namespace Humans.Base.Interfaces.Demo
            {
                public interface INewSomethingInvalidator : Humans.Base.Interfaces.IInvalidator
                {
                    void Invalidate();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new InvalidatorInterfaceRatchetAnalyzer(),
            "Humans.Application",
            source);

        var hits = diagnostics.Where(IsHum0028).ToList();
        hits.Should().ContainSingle();
        hits[0].Severity.Should().Be(DiagnosticSeverity.Error);
    }

    [HumansFact]
    public async Task Analyze_GrandfatheredInterface_Warns()
    {
        var source = Stubs + """

            namespace Humans.Base.Interfaces.Demo
            {
                [Humans.Base.Attributes.Grandfathered(
                    ruleId: "HUM0028",
                    justification: "Pre-existing invalidator awaiting absorption into caching decorator.",
                    since: "2026-05-27",
                    issueRef: "nobodies-collective/Humans#805")]
                public interface IExistingInvalidator : Humans.Base.Interfaces.IInvalidator
                {
                    void Invalidate();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new InvalidatorInterfaceRatchetAnalyzer(),
            "Humans.Application",
            source);

        var hits = diagnostics.Where(IsHum0028).ToList();
        hits.Should().ContainSingle();
        hits[0].Severity.Should().Be(DiagnosticSeverity.Warning);
    }

    [HumansFact]
    public async Task Analyze_GrandfatheredOtherRule_Fires()
    {
        var source = Stubs + """

            namespace Humans.Base.Interfaces.Demo
            {
                [Humans.Base.Attributes.Grandfathered(
                    ruleId: "HUM0042",
                    justification: "Different rule.",
                    since: "2026-05-27",
                    issueRef: "nobodies-collective/Humans#0")]
                public interface IWrongRuleInvalidator : Humans.Base.Interfaces.IInvalidator
                {
                    void Invalidate();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new InvalidatorInterfaceRatchetAnalyzer(),
            "Humans.Application",
            source);

        var hits = diagnostics.Where(IsHum0028).ToList();
        hits.Should().ContainSingle();
        hits[0].Severity.Should().Be(DiagnosticSeverity.Error);
    }

    [HumansFact]
    public async Task Analyze_MarkerItself_IsSilent()
    {
        // The marker only declares itself; the analyzer must not double-flag it.
        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new InvalidatorInterfaceRatchetAnalyzer(),
            "Humans.Application",
            Stubs);

        diagnostics.Where(IsHum0028).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Analyze_UnrelatedInterface_IsSilent()
    {
        var source = Stubs + """

            namespace Humans.Base.Interfaces.Demo
            {
                public interface IPlainService
                {
                    void Do();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new InvalidatorInterfaceRatchetAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0028).Should().BeEmpty();
    }

}
