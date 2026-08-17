using AwesomeAssertions;

namespace Humans.Analyzers.Tests;

public class SectionRulesAnalyzerTests
{
    // What makes the compilation a section: the entry point AssemblyScope.IsSection looks
    // up as <assemblyName>.Section. Prefixed to every source that needs one.
    private const string SectionEntryPoint = """
        namespace Humans.Test
        {
            public sealed class Section : Humans.Application.Interfaces.ISection { }
        }

        """;

    // Marker/base types the analyzer carves out live in other assemblies in production, so
    // they are compiled here as a referenced assembly rather than as symbols of the
    // compilation under test.
    private const string ReferencedStubs = """
        namespace Humans.Application.Interfaces
        {
            public interface ISection { }

            public interface IRecurringJob
            {
                System.Threading.Tasks.Task ExecuteAsync(System.Threading.CancellationToken cancellationToken = default);
            }
        }

        namespace Microsoft.EntityFrameworkCore.Migrations
        {
            public abstract class Migration { }
        }

        namespace Microsoft.AspNetCore.Mvc
        {
            [System.AttributeUsage(System.AttributeTargets.Class)]
            public sealed class ViewComponentAttribute : System.Attribute
            {
                public string Name { get; set; }
            }

            [System.AttributeUsage(System.AttributeTargets.Class)]
            public sealed class NonViewComponentAttribute : System.Attribute { }
        }

        namespace Microsoft.AspNetCore.Razor.TagHelpers
        {
            public interface ITagHelper { }
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
        string.Equals(d.Id, "HUM0034", StringComparison.Ordinal);

    [HumansFact]
    public async Task Fires_on_public_type_with_no_carve_out()
    {
        var source = SectionEntryPoint + """

            namespace Humans.Test
            {
                public sealed class LeakedType { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Test",
            source,
            ReferencedStubs);

        diagnostics.Should().ContainSingle(d => IsHum0034(d));
    }

    [HumansFact]
    public async Task Does_not_fire_on_internal_type()
    {
        var source = SectionEntryPoint + """

            namespace Humans.Test
            {
                internal sealed class NotLeaked { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Test",
            source,
            ReferencedStubs);

        diagnostics.Where(IsHum0034).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_on_the_ISection_entry_point()
    {
        var source = SectionEntryPoint;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Test",
            source,
            ReferencedStubs);

        diagnostics.Where(IsHum0034).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_on_the_Resource_marker()
    {
        var source = SectionEntryPoint + """

            namespace Humans.Test
            {
                public class TestResource { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
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
        var source = SectionEntryPoint + """

            namespace Humans.Test
            {
                public sealed class ResourceLoader { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Test",
            source,
            ReferencedStubs);

        diagnostics.Should().ContainSingle(d => IsHum0034(d));
    }

    [HumansFact]
    public async Task Does_not_fire_on_type_under_Contracts_namespace()
    {
        var source = SectionEntryPoint + """

            namespace Humans.Test.Contracts
            {
                public interface ITestServiceRead { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Test",
            source,
            ReferencedStubs);

        diagnostics.Where(IsHum0034).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_on_IRecurringJob_implementor_under_Jobs()
    {
        var source = SectionEntryPoint + """

            namespace Humans.Test.Jobs
            {
                public sealed class SyncStuffJob : Humans.Application.Interfaces.IRecurringJob
                {
                    public System.Threading.Tasks.Task ExecuteAsync(System.Threading.CancellationToken cancellationToken = default) =>
                        System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Test",
            source,
            ReferencedStubs);

        diagnostics.Where(IsHum0034).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_on_public_Job_suffixed_type_under_Jobs()
    {
        // Enqueue-style Hangfire jobs (e.g. GateVendorCheckInJob) don't implement
        // IRecurringJob — only the *Job naming convention marks them.
        var source = SectionEntryPoint + """

            namespace Humans.Test.Jobs
            {
                public sealed class ProvisionThingJob { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Test",
            source,
            ReferencedStubs);

        diagnostics.Where(IsHum0034).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Fires_on_public_non_job_type_under_Jobs()
    {
        var source = SectionEntryPoint + """

            namespace Humans.Test.Jobs
            {
                public sealed class JobHelper { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Test",
            source,
            ReferencedStubs);

        diagnostics.Should().ContainSingle(d => IsHum0034(d));
    }

    [HumansFact]
    public async Task Does_not_fire_on_EF_migration()
    {
        var source = SectionEntryPoint + """

            namespace Humans.Test.Data.Migrations
            {
                public partial class BaselineTest : Microsoft.EntityFrameworkCore.Migrations.Migration { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Test",
            source,
            ReferencedStubs);

        diagnostics.Where(IsHum0034).Should().BeEmpty();
    }

    // Razor's build-time tag-helper discovery only sees public view components, so an
    // internal one renders <vc:…> as inert markup — green build, nothing on the page.
    // The carve-out matches ViewComponentConventions.IsComponent exactly.
    [HumansFact]
    public async Task Does_not_fire_on_view_component_named_by_convention()
    {
        var source = SectionEntryPoint + """

            namespace Humans.Test.ViewComponents
            {
                public sealed class ProfileCardViewComponent { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Test",
            source,
            ReferencedStubs);

        diagnostics.Where(IsHum0034).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_on_view_component_declared_by_attribute()
    {
        var source = SectionEntryPoint + """

            namespace Humans.Test.ViewComponents
            {
                [Microsoft.AspNetCore.Mvc.ViewComponent(Name = "ProfileCard")]
                public sealed class ProfileCardWidget { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Test",
            source,
            ReferencedStubs);

        diagnostics.Where(IsHum0034).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Fires_on_NonViewComponent_despite_the_name()
    {
        // [NonViewComponent] opts a conventionally-named class out of being a component,
        // so it is ordinary section internals again and the carve-out must not cover it.
        var source = SectionEntryPoint + """

            namespace Humans.Test.ViewComponents
            {
                [Microsoft.AspNetCore.Mvc.NonViewComponent]
                public sealed class HelperViewComponent { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Test",
            source,
            ReferencedStubs);

        diagnostics.Where(IsHum0034).Should().ContainSingle();
    }

    [HumansFact]
    public async Task Fires_on_public_type_whose_name_merely_contains_ViewComponent()
    {
        var source = SectionEntryPoint + """

            namespace Humans.Test.ViewComponents
            {
                public sealed class ViewComponentRegistry { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Test",
            source,
            ReferencedStubs);

        diagnostics.Where(IsHum0034).Should().ContainSingle();
    }

    // Razor's DefaultTagHelperDescriptorProvider applies the same public-only filter as
    // the view-component pass, with the same silent outcome: the element ships as inert
    // literal markup. No section declares a tag helper yet — the clause is written ahead
    // of its first subject so the rule states the principle, not one carve-out.
    [HumansFact]
    public async Task Does_not_fire_on_tag_helper()
    {
        var source = SectionEntryPoint + """

            namespace Humans.Test.TagHelpers
            {
                public sealed class BurnDateTagHelper : Microsoft.AspNetCore.Razor.TagHelpers.ITagHelper { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Test",
            source,
            ReferencedStubs);

        diagnostics.Where(IsHum0034).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Fires_on_public_type_named_TagHelper_that_implements_nothing()
    {
        // Implementing ITagHelper is the whole of the provider's test, so the name alone
        // carves nothing out — this one is ordinary section internals.
        var source = SectionEntryPoint + """

            namespace Humans.Test.TagHelpers
            {
                public sealed class BurnDateTagHelper { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Test",
            source,
            ReferencedStubs);

        diagnostics.Where(IsHum0034).Should().ContainSingle();
    }

    [HumansFact]
    public async Task Does_not_fire_on_tag_helper_whose_name_says_nothing()
    {
        // ...and conversely, discovery does not care what an implementation is called.
        var source = SectionEntryPoint + """

            namespace Humans.Test.TagHelpers
            {
                public sealed class BurnDateWidget : Microsoft.AspNetCore.Razor.TagHelpers.ITagHelper { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
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
            new SectionRulesAnalyzer(),
            "Humans.Test",
            source,
            ReferencedStubs);

        diagnostics.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Grandfathered_violator_downgrades_to_warning()
    {
        var source = SectionEntryPoint + """

            namespace Humans.Test
            {
                [Humans.Application.Architecture.Grandfathered("HUM0034", "test", "2026-08-10", "test")]
                public sealed class LeakedType { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Test",
            source,
            ReferencedStubs);

        var hum0034 = diagnostics.Where(IsHum0034).ToList();
        hum0034.Should().ContainSingle();
        hum0034[0].Severity.Should().Be(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);
    }

    private static bool IsHum0035(Microsoft.CodeAnalysis.Diagnostic d) =>
        string.Equals(d.Id, "HUM0035", StringComparison.Ordinal);

    private const string RepositoryStub = """

        namespace Humans.Application.Interfaces.Repositories
        {
            public interface IRepository { }
        }
        """;

    [HumansFact]
    public async Task Fires_on_repository_under_Contracts()
    {
        var source = SectionEntryPoint + RepositoryStub + """

            namespace Humans.Test.Contracts
            {
                public interface ITestRepository : Humans.Application.Interfaces.Repositories.IRepository { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Test",
            source,
            ReferencedStubs);

        diagnostics.Should().ContainSingle(d => IsHum0035(d));
    }

    [HumansFact]
    public async Task Does_not_fire_on_repository_outside_Contracts()
    {
        var source = SectionEntryPoint + RepositoryStub + """

            namespace Humans.Test.Data
            {
                internal interface ITestRepository : Humans.Application.Interfaces.Repositories.IRepository { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Test",
            source,
            ReferencedStubs);

        diagnostics.Where(IsHum0035).Should().BeEmpty();
    }
}
