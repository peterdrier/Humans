using AwesomeAssertions;
using Microsoft.CodeAnalysis;

namespace Humans.Analyzers.Tests;

public sealed class CrossSectionEfJoinAnalyzerTests
{
    private const string Stubs = """
        using System;

        namespace Microsoft.EntityFrameworkCore
        {
            public interface IEntityTypeConfiguration<TEntity>
            {
                void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<TEntity> builder);
            }
        }

        namespace Microsoft.EntityFrameworkCore.Metadata.Builders
        {
            public sealed class EntityTypeBuilder<TEntity>
            {
                public ReferenceNavigationBuilder HasOne<TRelatedEntity>() => new();
                public ReferenceNavigationBuilder HasOne<TRelatedEntity>(Func<TEntity, TRelatedEntity?> navigationExpression) => new();
                public CollectionNavigationBuilder HasMany<TRelatedEntity>() => new();
                public CollectionNavigationBuilder HasMany<TRelatedEntity>(Func<TEntity, System.Collections.Generic.IEnumerable<TRelatedEntity>> navigationExpression) => new();
            }

            public sealed class ReferenceNavigationBuilder { }
            public sealed class CollectionNavigationBuilder { }
        }

        namespace Humans.Application.Architecture
        {
            [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
            public sealed class GrandfatheredAttribute : System.Attribute
            {
                public GrandfatheredAttribute(string ruleId, string justification, string since, string issueRef) { }
            }
        }

        namespace Humans.Domain.Entities
        {
            public sealed class User { }
            public sealed class Team { }
            public sealed class TeamMember
            {
                public User User { get; set; } = new();
                public Team Team { get; set; } = new();
            }
        }

        namespace Humans.Infrastructure.Data.Configurations.Users
        {
            public sealed class UserConfiguration :
                Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Humans.Domain.Entities.User>
            {
                public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Humans.Domain.Entities.User> builder) { }
            }
        }

        namespace Humans.Infrastructure.Data.Configurations.Teams
        {
            public sealed class TeamConfiguration :
                Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Humans.Domain.Entities.Team>
            {
                public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Humans.Domain.Entities.Team> builder) { }
            }
        }
        """;

    private static bool IsHum0024(Diagnostic d) =>
        string.Equals(d.Id, CrossSectionEfJoinAnalyzer.DiagnosticId, StringComparison.Ordinal);

    [HumansFact]
    public async Task Fires_on_generic_cross_section_HasOne()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Data.Configurations.Teams
            {
                public sealed class TeamMemberConfiguration :
                    Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Humans.Domain.Entities.TeamMember>
                {
                    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Humans.Domain.Entities.TeamMember> builder) =>
                        builder.HasOne<Humans.Domain.Entities.User>();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new CrossSectionEfJoinAnalyzer(),
            "Humans.Infrastructure",
            source);

        var hit = diagnostics.Where(IsHum0024).Should().ContainSingle().Subject;
        hit.Severity.Should().Be(DiagnosticSeverity.Error);
    }

    [HumansFact]
    public async Task Reports_warning_for_grandfathered_configuration()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Data.Configurations.Teams
            {
                [Humans.Application.Architecture.Grandfathered(
                    ruleId: "HUM0024",
                    justification: "Pre-existing cross-section EF navigation join.",
                    since: "2026-05-25",
                    issueRef: "docs/architecture/roslyn-analysis.md#hum0024")]
                public sealed class TeamMemberConfiguration :
                    Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Humans.Domain.Entities.TeamMember>
                {
                    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Humans.Domain.Entities.TeamMember> builder) =>
                        builder.HasOne<Humans.Domain.Entities.User>();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new CrossSectionEfJoinAnalyzer(),
            "Humans.Infrastructure",
            source);

        var hit = diagnostics.Where(IsHum0024).Should().ContainSingle().Subject;
        hit.Severity.Should().Be(DiagnosticSeverity.Warning);
    }

    [HumansFact]
    public async Task Fires_on_lambda_cross_section_HasOne()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Data.Configurations.Teams
            {
                public sealed class TeamMemberConfiguration :
                    Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Humans.Domain.Entities.TeamMember>
                {
                    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Humans.Domain.Entities.TeamMember> builder) =>
                        builder.HasOne(member => member.User);
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new CrossSectionEfJoinAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Where(IsHum0024).Should().ContainSingle();
    }

    [HumansFact]
    public async Task Does_not_fire_on_same_section_navigation()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Data.Configurations.Teams
            {
                public sealed class TeamMemberConfiguration :
                    Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Humans.Domain.Entities.TeamMember>
                {
                    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Humans.Domain.Entities.TeamMember> builder) =>
                        builder.HasOne(member => member.Team);
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new CrossSectionEfJoinAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Where(IsHum0024).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_for_root_level_configuration_without_section_namespace()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Data.Configurations
            {
                public sealed class TeamMemberConfiguration :
                    Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Humans.Domain.Entities.TeamMember>
                {
                    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Humans.Domain.Entities.TeamMember> builder) =>
                        builder.HasOne<Humans.Domain.Entities.User>();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new CrossSectionEfJoinAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Where(IsHum0024).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_outside_infrastructure_assembly()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Data.Configurations.Teams
            {
                public sealed class TeamMemberConfiguration :
                    Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Humans.Domain.Entities.TeamMember>
                {
                    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Humans.Domain.Entities.TeamMember> builder) =>
                        builder.HasOne<Humans.Domain.Entities.User>();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new CrossSectionEfJoinAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0024).Should().BeEmpty();
    }
}
