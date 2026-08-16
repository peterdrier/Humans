using AwesomeAssertions;

namespace Humans.Analyzers.Tests;

public class CrossSectionReadRuleTests
{
    private const string Stubs = """
        namespace Humans.Application.Architecture
        {
            [System.AttributeUsage(System.AttributeTargets.Class)]
            public sealed class CrossSectionWriteAttribute : System.Attribute
            {
                public CrossSectionWriteAttribute(string reason) { Reason = reason; }
                public string Reason { get; }
            }
        }

        namespace Humans.Application.Interfaces.Teams
        {
            public interface ITeamServiceRead { string GetTeam(); }
            public interface ITeamService : ITeamServiceRead { void Rename(string name); }
        }
        """;

    private static bool IsHum0032(Microsoft.CodeAnalysis.Diagnostic d) =>
        string.Equals(d.Id, "HUM0032", StringComparison.Ordinal);

    [HumansFact]
    public async Task Fires_on_unmarked_cross_section_write_interface()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Camps
            {
                public sealed class CampService
                {
                    public CampService(Humans.Application.Interfaces.Teams.ITeamService teams) { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0032(d));
    }

    [HumansFact]
    public async Task Does_not_fire_when_the_class_declares_the_write()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Camps
            {
                [Humans.Application.Architecture.CrossSectionWrite("renames the team on camp rename")]
                public sealed class CampService
                {
                    public CampService(Humans.Application.Interfaces.Teams.ITeamService teams) { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0032).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_on_the_read_interface()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Camps
            {
                public sealed class CampService
                {
                    public CampService(Humans.Application.Interfaces.Teams.ITeamServiceRead teams) { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0032).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_within_the_owning_section()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Teams
            {
                public sealed class TeamAdminService
                {
                    public TeamAdminService(Humans.Application.Interfaces.Teams.ITeamService teams) { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0032).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_on_an_interface_with_no_read_base()
    {
        var source = """
            namespace Humans.Application.Interfaces.Teams
            {
                public interface ITeamService { void Rename(string name); }
            }

            namespace Humans.Application.Services.Camps
            {
                public sealed class CampService
                {
                    public CampService(Humans.Application.Interfaces.Teams.ITeamService teams) { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0032).Should().BeEmpty();
    }
}
