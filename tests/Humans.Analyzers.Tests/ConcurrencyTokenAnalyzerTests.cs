using AwesomeAssertions;

namespace Humans.Analyzers.Tests;

public sealed class ConcurrencyTokenAnalyzerTests
{
    private const string EfStub = """
        namespace Microsoft.EntityFrameworkCore.Metadata.Builders
        {
            public class PropertyBuilder
            {
                public PropertyBuilder IsConcurrencyToken() => this;
                public PropertyBuilder IsRowVersion() => this;
            }
        }
        """;

    // The analyzer downgrades to a warning when the containing type carries
    // [Grandfathered("HUM0007", …)]. The attribute is stubbed here so the
    // synthetic compilation can resolve it — the real build references it
    // transitively from Humans.Application.
    private const string GrandfatheredStub = """
        namespace Humans.Application.Architecture
        {
            [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
            public sealed class GrandfatheredAttribute : System.Attribute
            {
                public GrandfatheredAttribute(string ruleId, string justification, string since, string issueRef) { }
            }
        }
        """;

    private static bool IsHum0007(Microsoft.CodeAnalysis.Diagnostic d) =>
        string.Equals(d.Id, ConcurrencyTokenAnalyzer.DiagnosticId, StringComparison.Ordinal);

    [HumansFact]
    public async Task Fires_on_IsConcurrencyToken_in_live_infrastructure_source()
    {
        var source = EfStub + """

            namespace Humans.Infrastructure.Data.Configurations.Users
            {
                public class UserConfiguration
                {
                    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder builder) =>
                        builder.IsConcurrencyToken();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ConcurrencyTokenAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0007(d));
    }

    [HumansFact]
    public async Task Fires_on_IsRowVersion_in_live_infrastructure_source()
    {
        var source = EfStub + """

            namespace Humans.Infrastructure.Data.Configurations.Users
            {
                public class UserConfiguration
                {
                    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder builder) =>
                        builder.IsRowVersion();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ConcurrencyTokenAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0007(d));
    }

    [HumansFact]
    public async Task Fires_on_ConcurrencyCheck_attribute_in_live_domain_source()
    {
        var source = """
            using System.ComponentModel.DataAnnotations;

            namespace Humans.Domain.Entities
            {
                public class User
                {
                    [ConcurrencyCheck]
                    public string Name { get; set; } = "";
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ConcurrencyTokenAnalyzer(),
            "Humans.Domain",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0007(d));
    }

    [HumansFact]
    public async Task Fires_on_Timestamp_attribute_in_live_domain_source()
    {
        var source = """
            using System.ComponentModel.DataAnnotations;

            namespace Humans.Domain.Entities
            {
                public class User
                {
                    [Timestamp]
                    public byte[] Version { get; set; } = [];
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ConcurrencyTokenAnalyzer(),
            "Humans.Domain",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0007(d));
    }

    [HumansFact]
    public async Task Does_not_fire_in_migration_namespace()
    {
        var source = EfStub + """

            namespace Humans.Infrastructure.Migrations
            {
                public class ExistingSnapshot
                {
                    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder builder) =>
                        builder.IsConcurrencyToken();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ConcurrencyTokenAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_on_same_named_non_EF_method()
    {
        var source = """
            namespace Humans.Infrastructure.Services
            {
                public class LocalBuilder
                {
                    public void IsConcurrencyToken() { }
                }

                public class Caller
                {
                    public void Configure(LocalBuilder builder) => builder.IsConcurrencyToken();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ConcurrencyTokenAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_outside_production_assemblies()
    {
        var source = EfStub + """

            namespace Humans.Analyzers.Tests
            {
                public class TestOnly
                {
                    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder builder) =>
                        builder.IsRowVersion();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ConcurrencyTokenAnalyzer(),
            "Humans.Analyzers.Tests",
            source);

        diagnostics.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Downgrades_EF_call_to_warning_when_class_has_Grandfathered_for_HUM0007()
    {
        var source = EfStub + GrandfatheredStub + """

            namespace Humans.Infrastructure.Data.Configurations.Users
            {
                [Humans.Application.Architecture.Grandfathered(
                    ruleId: "HUM0007",
                    justification: "Pre-rule row-version pending removal.",
                    since: "2026-05-25",
                    issueRef: "nobodies-collective/Humans#0")]
                public class UserConfiguration
                {
                    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder builder) =>
                        builder.IsConcurrencyToken();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ConcurrencyTokenAnalyzer(),
            "Humans.Infrastructure",
            source);

        var hits = diagnostics.Where(IsHum0007).ToList();
        hits.Should().ContainSingle();
        hits[0].Severity.Should().Be(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);
    }

    [HumansFact]
    public async Task Downgrades_attribute_to_warning_when_class_has_Grandfathered_for_HUM0007()
    {
        var source = GrandfatheredStub + """

            namespace Humans.Domain.Entities
            {
                [Humans.Application.Architecture.Grandfathered(
                    ruleId: "HUM0007",
                    justification: "Pre-rule concurrency check pending removal.",
                    since: "2026-05-25",
                    issueRef: "nobodies-collective/Humans#0")]
                public class User
                {
                    [System.ComponentModel.DataAnnotations.ConcurrencyCheck]
                    public string Name { get; set; } = "";
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ConcurrencyTokenAnalyzer(),
            "Humans.Domain",
            source);

        var hits = diagnostics.Where(IsHum0007).ToList();
        hits.Should().ContainSingle();
        hits[0].Severity.Should().Be(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);
    }

    [HumansFact]
    public async Task Grandfathered_for_a_different_rule_still_fires_error()
    {
        var source = EfStub + GrandfatheredStub + """

            namespace Humans.Infrastructure.Data.Configurations.Users
            {
                [Humans.Application.Architecture.Grandfathered(
                    ruleId: "HUM0042",
                    justification: "Different rule.",
                    since: "2026-05-25",
                    issueRef: "nobodies-collective/Humans#0")]
                public class UserConfiguration
                {
                    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.PropertyBuilder builder) =>
                        builder.IsConcurrencyToken();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ConcurrencyTokenAnalyzer(),
            "Humans.Infrastructure",
            source);

        var hits = diagnostics.Where(IsHum0007).ToList();
        hits.Should().ContainSingle();
        hits[0].Severity.Should().Be(Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }
}
