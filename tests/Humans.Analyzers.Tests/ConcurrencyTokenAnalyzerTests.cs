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
}
