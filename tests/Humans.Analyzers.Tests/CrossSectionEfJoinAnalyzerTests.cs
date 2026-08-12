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
            public sealed class Profile
            {
                public User User { get; set; } = new();
            }
            public sealed class UserEmail
            {
                public User User { get; set; } = new();
            }
            public sealed class Team { }
            public sealed class TeamMember
            {
                public User User { get; set; } = new();
                public Team Team { get; set; } = new();
            }
            public sealed class SyncSettings { }
            public sealed class SyncLink
            {
                public SyncSettings Settings { get; set; } = new();
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

        namespace Humans.Infrastructure.Data.Configurations.Profiles
        {
            public sealed class ProfileConfiguration :
                Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Humans.Domain.Entities.Profile>
            {
                public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Humans.Domain.Entities.Profile> builder) { }
            }

            public sealed class UserEmailConfiguration :
                Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Humans.Domain.Entities.UserEmail>
            {
                public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Humans.Domain.Entities.UserEmail> builder) { }
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

    // Assembly attributes must precede every namespace/type declaration in the file
    // (CS1730), so this goes first in any source that needs the assembly under test
    // to be recognised as a section by AssemblyScope.IsSection. Self-contained
    // (no shared "Stubs" reuse, no "using System;") because a using-directive can't
    // legally follow an assembly attribute in the same compilation unit.
    private const string SectionAssemblyAttribute = """
        [assembly: Humans.Domain.Attributes.Section("Email")]

        """;

    private const string SectionScenarioStub = """

        namespace Humans.Domain.Attributes
        {
            [System.AttributeUsage(System.AttributeTargets.Assembly)]
            public sealed class SectionAttribute : System.Attribute
            {
                public SectionAttribute(string name) { Name = name; }
                public string Name { get; }
            }
        }

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
                public ReferenceNavigationBuilder HasOne<TRelatedEntity>(System.Func<TEntity, TRelatedEntity?> navigationExpression) => new();
            }

            public sealed class ReferenceNavigationBuilder { }
        }

        namespace Humans.Domain.Entities
        {
            public sealed class Team { }

            public sealed class EmailOutboxMessage
            {
                public Team Team { get; set; } = new();
                public EmailTemplate Template { get; set; } = new();
            }

            public sealed class EmailTemplate { }
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
    public async Task Does_not_fire_between_users_and_profiles_folded_section()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Data.Configurations.Profiles
            {
                public sealed class ProfileUserConfiguration :
                    Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Humans.Domain.Entities.Profile>
                {
                    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Humans.Domain.Entities.Profile> builder) =>
                        builder.HasOne(profile => profile.User);
                }

                public sealed class UserEmailUserConfiguration :
                    Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Humans.Domain.Entities.UserEmail>
                {
                    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Humans.Domain.Entities.UserEmail> builder) =>
                        builder.HasOne(email => email.User);
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
    public async Task Fires_for_root_level_configuration_targeting_sectioned_entity()
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

        var hit = diagnostics.Where(IsHum0024).Should().ContainSingle().Subject;
        hit.Severity.Should().Be(DiagnosticSeverity.Error);
        hit.GetMessage().Should().Contain("(unsectioned)");
    }

    [HumansFact]
    public async Task Fires_for_sectioned_configuration_targeting_root_level_entity()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Data.Configurations
            {
                public sealed class SyncSettingsConfiguration :
                    Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Humans.Domain.Entities.SyncSettings>
                {
                    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Humans.Domain.Entities.SyncSettings> builder) { }
                }
            }

            namespace Humans.Infrastructure.Data.Configurations.Teams
            {
                public sealed class TeamMemberConfiguration :
                    Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Humans.Domain.Entities.TeamMember>
                {
                    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Humans.Domain.Entities.TeamMember> builder) =>
                        builder.HasOne<Humans.Domain.Entities.SyncSettings>();
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
    public async Task Does_not_fire_between_two_root_level_configurations()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Data.Configurations
            {
                public sealed class SyncSettingsConfiguration :
                    Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Humans.Domain.Entities.SyncSettings>
                {
                    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Humans.Domain.Entities.SyncSettings> builder) { }
                }

                public sealed class SyncLinkConfiguration :
                    Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Humans.Domain.Entities.SyncLink>
                {
                    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Humans.Domain.Entities.SyncLink> builder) =>
                        builder.HasOne(link => link.Settings);
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
    public async Task Reports_warning_for_grandfathered_root_level_configuration()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Data.Configurations
            {
                [Humans.Application.Architecture.Grandfathered(
                    ruleId: "HUM0024",
                    justification: "Pre-existing cross-section EF navigation join.",
                    since: "2026-08-05",
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
    public async Task Fires_for_a_section_assemblys_own_configuration_joining_another_sections_entity()
    {
        // Section project (nobodies-collective/Humans#866, G5): configs live outside
        // the Infrastructure prefix (e.g. Humans.Email.Data), so ownership resolution
        // must fall back to the assembly's [assembly: Section("…")] marker for the
        // map to populate at all -- without the fallback, OnCompilationStart bails
        // early on an empty map and the rule never fires inside a section compilation.
        var source = SectionAssemblyAttribute + SectionScenarioStub + """

            namespace Humans.Email.Data
            {
                public sealed class EmailOutboxMessageConfiguration :
                    Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Humans.Domain.Entities.EmailOutboxMessage>
                {
                    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Humans.Domain.Entities.EmailOutboxMessage> builder) =>
                        builder.HasOne(m => m.Team);
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new CrossSectionEfJoinAnalyzer(),
            "Humans.Email",
            source);

        var hit = diagnostics.Where(IsHum0024).Should().ContainSingle().Subject;
        hit.GetMessage().Should().Contain("Email");
        hit.GetMessage().Should().Contain("Teams");
    }

    [HumansFact]
    public async Task Does_not_fire_for_a_section_assemblys_own_intra_section_join()
    {
        // Both configs resolve via the same assembly-level fallback ("Email"), so
        // this proves the fallback populates the ownership map without flagging a
        // same-section join as a false positive.
        var source = SectionAssemblyAttribute + SectionScenarioStub + """

            namespace Humans.Email.Data
            {
                public sealed class EmailTemplateConfiguration :
                    Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Humans.Domain.Entities.EmailTemplate>
                {
                    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Humans.Domain.Entities.EmailTemplate> builder) { }
                }

                public sealed class EmailOutboxMessageConfiguration :
                    Microsoft.EntityFrameworkCore.IEntityTypeConfiguration<Humans.Domain.Entities.EmailOutboxMessage>
                {
                    public void Configure(Microsoft.EntityFrameworkCore.Metadata.Builders.EntityTypeBuilder<Humans.Domain.Entities.EmailOutboxMessage> builder) =>
                        builder.HasOne(m => m.Template);
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new CrossSectionEfJoinAnalyzer(),
            "Humans.Email",
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
