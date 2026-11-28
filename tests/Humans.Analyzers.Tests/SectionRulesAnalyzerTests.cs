using AwesomeAssertions;

namespace Humans.Analyzers.Tests;

public class SectionRulesAnalyzerTests
{
    // What makes the compilation a section: the entry point AssemblyScope.IsSection looks
    // up as <assemblyName>.Section. Prefixed to every source that needs one.
    private const string SectionEntryPoint = """
        namespace Humans.Test
        {
            public sealed class Section : Humans.Base.Interfaces.ISection { }
        }

        """;

    // Marker/base types the analyzer carves out live in other assemblies in production, so
    // they are compiled here as a referenced assembly rather than as symbols of the
    // compilation under test.
    private const string ReferencedStubs = """
        namespace Humans.Base.Interfaces
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

        namespace Humans.Base.Attributes
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
    public async Task Analyze_PublicTypeWithoutExemption_Fires()
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
    public async Task Analyze_InternalType_IsSilent()
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
    public async Task Analyze_SectionEntryPoint_IsSilent()
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
    public async Task Analyze_ResourceMarker_IsSilent()
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
    public async Task Analyze_ResourceInNameOnly_Fires()
    {
        // Only a name ENDING in "Resource" is the carve-out (the runtime SectionResourceTypes() matcher is stricter: exactly <Section>Resource).
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
    public async Task Analyze_TypeUnderContracts_IsSilent()
    {
        var source = SectionEntryPoint + """

            namespace Humans.Test.Contracts
            {
                public interface ITestService { }
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
    public async Task Analyze_ManagementInterfaceUnderContracts_Fires()
    {
        var source = SectionEntryPoint + """

            namespace Humans.Test.Contracts
            {
                public interface ITestManagement { }
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
    public async Task Analyze_ManagementInterfaceInLeaf_Fires()
    {
        const string source = """
            namespace Humans.Test.Contracts
            {
                public interface ITestWrite { }
                public interface ITestManagement { }
                internal interface IOtherManagement { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Test.Contracts",
            source,
            ReferencedStubs);

        diagnostics.Where(IsHum0034).Should().ContainSingle()
            .Which.GetMessage().Should().Contain("ITestManagement");
    }

    [HumansFact]
    public async Task Analyze_RecurringJobUnderJobs_IsSilent()
    {
        var source = SectionEntryPoint + """

            namespace Humans.Test.Jobs
            {
                public sealed class SyncStuffJob : Humans.Base.Interfaces.IRecurringJob
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
    public async Task Analyze_JobSuffixedTypeUnderJobs_IsSilent()
    {
        // Enqueue-style Hangfire jobs don't implement
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
    public async Task Analyze_NonJobTypeUnderJobs_Fires()
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
    public async Task Analyze_EfMigration_IsSilent()
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
    public async Task Analyze_ViewComponentByConvention_IsSilent()
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
    public async Task Analyze_ViewComponentByAttribute_IsSilent()
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
    public async Task Analyze_NonViewComponent_Fires()
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
    public async Task Analyze_ViewComponentInNameOnly_Fires()
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
    public async Task Analyze_TagHelper_IsSilent()
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
    public async Task Analyze_TagHelperInNameOnly_Fires()
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
    public async Task Analyze_TagHelperWithPlainName_IsSilent()
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
    public async Task Analyze_OutsideSectionAssembly_IsSilent()
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
    public async Task Analyze_GrandfatheredViolator_Warns()
    {
        var source = SectionEntryPoint + """

            namespace Humans.Test
            {
                [Humans.Base.Attributes.Grandfathered("HUM0034", "test", "2026-08-10", "test")]
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

        namespace Humans.Base.Interfaces.Repositories
        {
            public interface IRepository { }
        }
        """;

    [HumansFact]
    public async Task Analyze_RepositoryUnderContracts_Fires()
    {
        var source = SectionEntryPoint + RepositoryStub + """

            namespace Humans.Test.Contracts
            {
                public interface ITestRepository : Humans.Base.Interfaces.Repositories.IRepository { }
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
    public async Task Analyze_RepositoryOutsideContracts_IsSilent()
    {
        var source = SectionEntryPoint + RepositoryStub + """

            namespace Humans.Test.Data
            {
                internal interface ITestRepository : Humans.Base.Interfaces.Repositories.IRepository { }
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
