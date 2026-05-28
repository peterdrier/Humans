using AwesomeAssertions;
using Microsoft.CodeAnalysis;

namespace Humans.Analyzers.Tests;

public class InvalidatorInterfaceRatchetAnalyzerTests
{
    // IInvalidator lives in Humans.Application.Interfaces; GrandfatheredAttribute
    // (widened to AttributeTargets.Class | Interface so HUM0028 can grandfather
    // interface declarations) in Humans.Application.Architecture. Stubs mirror
    // the production shapes so the analyzer can resolve them.
    private const string Stubs = """
        namespace Humans.Application.Interfaces
        {
            public interface IInvalidator { }
        }

        namespace Humans.Application.Architecture
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
    public async Task Fires_error_on_new_invalidator_interface_without_grandfather()
    {
        var source = Stubs + """

            namespace Humans.Application.Interfaces.Demo
            {
                public interface INewSomethingInvalidator : Humans.Application.Interfaces.IInvalidator
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
    public async Task Downgrades_to_warning_when_interface_has_Grandfathered_for_HUM0028()
    {
        var source = Stubs + """

            namespace Humans.Application.Interfaces.Demo
            {
                [Humans.Application.Architecture.Grandfathered(
                    ruleId: "HUM0028",
                    justification: "Pre-existing invalidator awaiting absorption into caching decorator.",
                    since: "2026-05-27",
                    issueRef: "nobodies-collective/Humans#805")]
                public interface IExistingInvalidator : Humans.Application.Interfaces.IInvalidator
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
    public async Task Grandfathered_for_a_different_rule_still_fires_error()
    {
        var source = Stubs + """

            namespace Humans.Application.Interfaces.Demo
            {
                [Humans.Application.Architecture.Grandfathered(
                    ruleId: "HUM0042",
                    justification: "Different rule.",
                    since: "2026-05-27",
                    issueRef: "nobodies-collective/Humans#0")]
                public interface IWrongRuleInvalidator : Humans.Application.Interfaces.IInvalidator
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
    public async Task Does_not_fire_on_the_IInvalidator_marker_itself()
    {
        // The marker only declares itself; the analyzer must not double-flag it.
        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new InvalidatorInterfaceRatchetAnalyzer(),
            "Humans.Application",
            Stubs);

        diagnostics.Where(IsHum0028).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_on_interface_that_does_not_extend_IInvalidator()
    {
        var source = Stubs + """

            namespace Humans.Application.Interfaces.Demo
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

    [HumansFact]
    public async Task Does_not_fire_outside_Application_assembly()
    {
        var source = Stubs + """

            namespace Humans.Web.Demo
            {
                public interface INewInvalidator : Humans.Application.Interfaces.IInvalidator
                {
                    void Invalidate();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new InvalidatorInterfaceRatchetAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Where(IsHum0028).Should().BeEmpty();
    }
}
