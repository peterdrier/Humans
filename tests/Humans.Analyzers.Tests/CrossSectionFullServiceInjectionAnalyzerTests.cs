using AwesomeAssertions;
using Microsoft.CodeAnalysis;

namespace Humans.Analyzers.Tests;

public class CrossSectionFullServiceInjectionAnalyzerTests
{
    // Mirrors the production shapes: the IApplicationService marker, the
    // GrandfatheredAttribute, and a Teams section with the read/write split
    // (ITeamService : ITeamServiceRead). Read-interface section comes from the
    // Humans.Application.Interfaces.<Section> namespace; caller section from
    // Humans.Application.Services.<Section>.
    private const string Stubs = """
        namespace Humans.Application.Interfaces
        {
            public interface IApplicationService { }
        }

        namespace Humans.Application.Architecture
        {
            public sealed class GrandfatheredAttribute : System.Attribute
            {
                public GrandfatheredAttribute(string ruleId, string justification, string since, string issueRef) { }
            }
        }

        namespace Humans.Application.Interfaces.Teams
        {
            public interface ITeamServiceRead
            {
                System.Threading.Tasks.Task<int> GetTeamCountAsync();
                string DefaultTeamName { get; }
            }

            public interface ITeamService : ITeamServiceRead, Humans.Application.Interfaces.IApplicationService
            {
                System.Threading.Tasks.Task AddMemberAsync(System.Guid userId);
            }
        }
        """;

    private static bool IsHum0032(Diagnostic d) =>
        string.Equals(d.Id, CrossSectionFullServiceInjectionAnalyzer.DiagnosticId, StringComparison.Ordinal);

    [HumansFact]
    public async Task Fires_error_when_cross_section_class_only_uses_read_members()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Expenses
            {
                public sealed class ExpenseSummaryService(Humans.Application.Interfaces.Teams.ITeamService teams)
                {
                    public System.Threading.Tasks.Task<int> CountAsync() => teams.GetTeamCountAsync();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new CrossSectionFullServiceInjectionAnalyzer(),
            "Humans.Application",
            source);

        var diagnostic = diagnostics.Should().ContainSingle(d => IsHum0032(d)).Subject;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Error);
        diagnostic.GetMessage().Should().Contain("ITeamServiceRead");
    }

    [HumansFact]
    public async Task Fires_when_only_a_read_property_is_used_via_classic_field_pattern()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Expenses
            {
                public sealed class ExpenseLabelService
                {
                    private readonly Humans.Application.Interfaces.Teams.ITeamService _teams;

                    public ExpenseLabelService(Humans.Application.Interfaces.Teams.ITeamService teams)
                    {
                        _teams = teams;
                    }

                    public string Label() => _teams.DefaultTeamName;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new CrossSectionFullServiceInjectionAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0032(d));
    }

    [HumansFact]
    public async Task Fires_when_wiring_uses_a_null_guard_coalesce()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Expenses
            {
                public sealed class ExpenseGuardedService
                {
                    private readonly Humans.Application.Interfaces.Teams.ITeamService _teams;

                    public ExpenseGuardedService(Humans.Application.Interfaces.Teams.ITeamService teams)
                    {
                        _teams = teams ?? throw new System.ArgumentNullException(nameof(teams));
                    }

                    public System.Threading.Tasks.Task<int> CountAsync() => _teams.GetTeamCountAsync();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new CrossSectionFullServiceInjectionAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0032(d));
    }

    [HumansFact]
    public async Task Does_not_fire_when_a_write_member_is_used()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Expenses
            {
                public sealed class ExpenseProvisioningService(Humans.Application.Interfaces.Teams.ITeamService teams)
                {
                    public System.Threading.Tasks.Task ProvisionAsync(System.Guid userId) => teams.AddMemberAsync(userId);
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new CrossSectionFullServiceInjectionAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0032).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_for_same_section_injection()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Teams
            {
                public sealed class TeamPageService(Humans.Application.Interfaces.Teams.ITeamService teams)
                {
                    public System.Threading.Tasks.Task<int> CountAsync() => teams.GetTeamCountAsync();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new CrossSectionFullServiceInjectionAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0032).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_across_the_users_profiles_fold()
    {
        var source = Stubs + """

            namespace Humans.Application.Interfaces.Users
            {
                public interface IUserServiceRead
                {
                    System.Threading.Tasks.Task<string> GetDisplayNameAsync(System.Guid id);
                }

                public interface IUserService : IUserServiceRead, Humans.Application.Interfaces.IApplicationService
                {
                    System.Threading.Tasks.Task SuspendAsync(System.Guid id);
                }
            }

            namespace Humans.Application.Services.Profiles
            {
                public sealed class ProfileBadgeService(Humans.Application.Interfaces.Users.IUserService users)
                {
                    public System.Threading.Tasks.Task<string> NameAsync(System.Guid id) => users.GetDisplayNameAsync(id);
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new CrossSectionFullServiceInjectionAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0032).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_when_the_read_interface_is_injected()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Expenses
            {
                public sealed class ExpenseSummaryService(Humans.Application.Interfaces.Teams.ITeamServiceRead teams)
                {
                    public System.Threading.Tasks.Task<int> CountAsync() => teams.GetTeamCountAsync();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new CrossSectionFullServiceInjectionAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0032).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_when_the_dependency_escapes_as_an_argument()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Expenses
            {
                public static class Helper
                {
                    public static System.Threading.Tasks.Task<int> CountAsync(Humans.Application.Interfaces.Teams.ITeamService teams)
                        => teams.GetTeamCountAsync();
                }

                public sealed class ExpenseSummaryService(Humans.Application.Interfaces.Teams.ITeamService teams)
                {
                    public System.Threading.Tasks.Task<int> CountAsync() => Helper.CountAsync(teams);
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new CrossSectionFullServiceInjectionAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0032).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_when_no_read_split_exists()
    {
        var source = Stubs + """

            namespace Humans.Application.Interfaces.Shifts
            {
                public interface IShiftService : Humans.Application.Interfaces.IApplicationService
                {
                    System.Threading.Tasks.Task<int> GetShiftCountAsync();
                }
            }

            namespace Humans.Application.Services.Expenses
            {
                public sealed class ExpenseShiftService(Humans.Application.Interfaces.Shifts.IShiftService shifts)
                {
                    public System.Threading.Tasks.Task<int> CountAsync() => shifts.GetShiftCountAsync();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new CrossSectionFullServiceInjectionAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0032).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Downgrades_to_warning_when_class_is_grandfathered()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Expenses
            {
                [Humans.Application.Architecture.Grandfathered(
                    "HUM0032", "Pre-existing read-only full-service injection.", "2026-06-09", "nobodies-collective/Humans#857")]
                public sealed class ExpenseSummaryService(Humans.Application.Interfaces.Teams.ITeamService teams)
                {
                    public System.Threading.Tasks.Task<int> CountAsync() => teams.GetTeamCountAsync();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new CrossSectionFullServiceInjectionAnalyzer(),
            "Humans.Application",
            source);

        var diagnostic = diagnostics.Should().ContainSingle(d => IsHum0032(d)).Subject;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Warning);
    }

    [HumansFact]
    public async Task Does_not_fire_outside_Application_assembly()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Expenses
            {
                public sealed class ExpenseSummaryService(Humans.Application.Interfaces.Teams.ITeamService teams)
                {
                    public System.Threading.Tasks.Task<int> CountAsync() => teams.GetTeamCountAsync();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new CrossSectionFullServiceInjectionAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Where(IsHum0032).Should().BeEmpty();
    }
}
