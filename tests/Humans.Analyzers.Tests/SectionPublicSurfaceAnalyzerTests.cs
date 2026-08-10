using AwesomeAssertions;

namespace Humans.Analyzers.Tests;

public class SectionPublicSurfaceAnalyzerTests
{
    // Assembly attributes must precede every namespace/type declaration in the file
    // (CS1730), so this goes first in every source that needs the assembly under
    // test to be recognised as a section by AssemblyScope.IsSection.
    private const string SectionAssemblyAttribute = """
        [assembly: Humans.Domain.Attributes.Section("Test")]

        """;

    // Marker/base types the analyzer carves out live in other assemblies in
    // production (ISection in Humans.Interfaces, Migration in
    // Microsoft.EntityFrameworkCore) — compiled here as a separate referenced
    // assembly so implementing/deriving from them doesn't also make them symbols
    // declared in, and analyzed as part of, the compilation under test.
    private const string ReferencedStubs = """
        namespace Humans.Domain.Attributes
        {
            [System.AttributeUsage(System.AttributeTargets.Assembly | System.AttributeTargets.Interface | System.AttributeTargets.Class)]
            public sealed class SectionAttribute : System.Attribute
            {
                public SectionAttribute(string name) { Name = name; }
                public string Name { get; }
            }
        }

        namespace Humans.Application.Interfaces
        {
            public interface ISection { }
        }

        namespace Microsoft.EntityFrameworkCore.Migrations
        {
            public abstract class Migration { }
        }

        namespace Humans.Application.Architecture
        {
            [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = true)]
            public sealed class GrandfatheredAttribute : System.Attribute
            {
                public GrandfatheredAttribute(string ruleId, string justification, string since, string issueRef) { }
            }
        }
        """;

    private static bool IsHum0034(Microsoft.CodeAnalysis.Diagnostic d) =>
        string.Equals(d.Id, SectionPublicSurfaceAnalyzer.DiagnosticId, StringComparison.Ordinal);

    [HumansFact]
    public async Task Fires_on_public_type_with_no_carve_out()
    {
        var source = SectionAssemblyAttribute + """

            namespace Humans.Test
            {
                public sealed class LeakedType { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionPublicSurfaceAnalyzer(),
            "Humans.Test",
            source,
            ReferencedStubs);

        diagnostics.Should().ContainSingle(d => IsHum0034(d));
    }

    [HumansFact]
    public async Task Does_not_fire_on_internal_type()
    {
        var source = SectionAssemblyAttribute + """

            namespace Humans.Test
            {
                internal sealed class NotLeaked { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionPublicSurfaceAnalyzer(),
            "Humans.Test",
            source,
            ReferencedStubs);

        diagnostics.Where(IsHum0034).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_on_the_ISection_entry_point()
    {
        var source = SectionAssemblyAttribute + """

            namespace Humans.Test
            {
                public sealed class Section : Humans.Application.Interfaces.ISection { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionPublicSurfaceAnalyzer(),
            "Humans.Test",
            source,
            ReferencedStubs);

        diagnostics.Where(IsHum0034).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_on_the_Resource_marker()
    {
        var source = SectionAssemblyAttribute + """

            namespace Humans.Test
            {
                public class TestResource { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionPublicSurfaceAnalyzer(),
            "Humans.Test",
            source,
            ReferencedStubs);

        diagnostics.Where(IsHum0034).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Fires_on_public_type_whose_name_merely_contains_Resource()
    {
        // Only a name ENDING in "Resource" is the carve-out — same filter
        // SectionResourceTypes() applies at runtime.
        var source = SectionAssemblyAttribute + """

            namespace Humans.Test
            {
                public sealed class ResourceLoader { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionPublicSurfaceAnalyzer(),
            "Humans.Test",
            source,
            ReferencedStubs);

        diagnostics.Should().ContainSingle(d => IsHum0034(d));
    }

    [HumansFact]
    public async Task Does_not_fire_on_type_under_Contracts_namespace()
    {
        var source = SectionAssemblyAttribute + """

            namespace Humans.Test.Contracts
            {
                public interface ITestServiceRead { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionPublicSurfaceAnalyzer(),
            "Humans.Test",
            source,
            ReferencedStubs);

        diagnostics.Where(IsHum0034).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_on_EF_migration()
    {
        var source = SectionAssemblyAttribute + """

            namespace Humans.Test.Data.Migrations
            {
                public partial class BaselineTest : Microsoft.EntityFrameworkCore.Migrations.Migration { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionPublicSurfaceAnalyzer(),
            "Humans.Test",
            source,
            ReferencedStubs);

        diagnostics.Where(IsHum0034).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_outside_a_section_assembly()
    {
        var source = """
            namespace Humans.Test
            {
                public sealed class LeakedType { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionPublicSurfaceAnalyzer(),
            "Humans.Test",
            source,
            ReferencedStubs);

        diagnostics.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Grandfathered_violator_downgrades_to_warning()
    {
        var source = SectionAssemblyAttribute + """

            namespace Humans.Test
            {
                [Humans.Application.Architecture.Grandfathered("HUM0034", "test", "2026-08-10", "test")]
                public sealed class LeakedType { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionPublicSurfaceAnalyzer(),
            "Humans.Test",
            source,
            ReferencedStubs);

        var hum0034 = diagnostics.Where(IsHum0034).ToList();
        hum0034.Should().ContainSingle();
        hum0034[0].Severity.Should().Be(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);
    }
}
