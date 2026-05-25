using AwesomeAssertions;

namespace Humans.Analyzers.Tests;

public class ApplicationServiceLocationAnalyzerTests
{
    private const string Stubs = """
        namespace Humans.Application.Interfaces
        {
            public interface IApplicationService { }
        }

        namespace Humans.Application.Interfaces.Camps
        {
            public interface ICampService : Humans.Application.Interfaces.IApplicationService { }
        }

        namespace Humans.Application.Interfaces.Teams
        {
            public interface ITeamService : Humans.Application.Interfaces.IApplicationService { }
        }
        """;

    private static bool IsHum0012(Microsoft.CodeAnalysis.Diagnostic d) =>
        string.Equals(d.Id, ApplicationServiceLocationAnalyzer.DiagnosticId, StringComparison.Ordinal);

    [HumansFact]
    public async Task Does_not_fire_when_service_lives_under_matching_section()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Camps
            {
                public sealed class CampService : Humans.Application.Interfaces.Camps.ICampService { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ApplicationServiceLocationAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0012).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_when_service_lives_in_nested_subnamespace_of_section()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Camps.Internal
            {
                public sealed class CampHelperService : Humans.Application.Interfaces.Camps.ICampService { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ApplicationServiceLocationAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0012).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Fires_when_camps_service_moves_to_teams_namespace()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Teams
            {
                public sealed class CampService : Humans.Application.Interfaces.Camps.ICampService { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ApplicationServiceLocationAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0012(d));
    }

    [HumansFact]
    public async Task Fires_when_service_lives_outside_Services_namespace()
    {
        var source = Stubs + """

            namespace Humans.Application.Camps
            {
                public sealed class CampService : Humans.Application.Interfaces.Camps.ICampService { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ApplicationServiceLocationAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0012(d));
    }

    [HumansFact]
    public async Task Does_not_fire_on_non_service_class()
    {
        var source = """
            namespace Humans.Application.Interfaces
            {
                public interface IApplicationService { }
            }

            namespace Humans.Application.Some.Other.Place
            {
                public sealed class JustAHelper { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ApplicationServiceLocationAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0012).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_on_abstract_service_base_class()
    {
        var source = Stubs + """

            namespace Humans.Application.Internal
            {
                public abstract class CampServiceBase : Humans.Application.Interfaces.Camps.ICampService { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ApplicationServiceLocationAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0012).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Accepts_service_when_one_of_multiple_section_interfaces_matches()
    {
        // A class implementing two service interfaces from different sections
        // is acceptable if it lives in either section's namespace.
        var source = Stubs + """

            namespace Humans.Application.Services.Camps
            {
                public sealed class HybridService :
                    Humans.Application.Interfaces.Camps.ICampService,
                    Humans.Application.Interfaces.Teams.ITeamService { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ApplicationServiceLocationAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0012).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Fires_when_class_implements_section_interfaces_but_lives_in_neither_section()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Onboarding
            {
                public sealed class HybridService :
                    Humans.Application.Interfaces.Camps.ICampService,
                    Humans.Application.Interfaces.Teams.ITeamService { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ApplicationServiceLocationAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0012(d));
    }

    [HumansFact]
    public async Task Top_level_service_interface_accepts_any_Services_subnamespace()
    {
        // Real-world case: `IAgentService` lives in `Humans.Application.Interfaces`
        // directly, and `AgentService` lives in `Humans.Application.Services.Agent`.
        var source = """
            namespace Humans.Application.Interfaces
            {
                public interface IApplicationService { }
                public interface IAgentService : IApplicationService { }
            }

            namespace Humans.Application.Services.Agent
            {
                public sealed class AgentService : Humans.Application.Interfaces.IAgentService { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ApplicationServiceLocationAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0012).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Fires_when_service_implements_marker_indirectly_in_wrong_namespace()
    {
        var source = """
            namespace Humans.Application.Interfaces
            {
                public interface IApplicationService { }
            }

            namespace Humans.Application.Interfaces.Mid
            {
                public interface IMidService : Humans.Application.Interfaces.IApplicationService { }
            }

            namespace Humans.Application.Interfaces.Deep
            {
                public interface IDeepService : Humans.Application.Interfaces.Mid.IMidService { }
            }

            namespace Humans.Application.Services.Wrong
            {
                public sealed class DeepService : Humans.Application.Interfaces.Deep.IDeepService { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ApplicationServiceLocationAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0012(d));
    }

    [HumansFact]
    public async Task Grandfathered_violator_downgrades_to_warning()
    {
        var source = Stubs + """

            namespace Humans.Application.Architecture
            {
                [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = true)]
                public sealed class GrandfatheredAttribute : System.Attribute
                {
                    public GrandfatheredAttribute(string ruleId, string justification, string since, string issueRef) { }
                }
            }

            namespace Humans.Application.Services.Teams
            {
                [Humans.Application.Architecture.Grandfathered("HUM0012", "test", "2026-05-15", "test")]
                public sealed class CampService : Humans.Application.Interfaces.Camps.ICampService { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ApplicationServiceLocationAnalyzer(),
            "Humans.Application",
            source);

        var hum0012 = diagnostics.Where(IsHum0012).ToList();
        hum0012.Should().ContainSingle();
        hum0012[0].Severity.Should().Be(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);
    }

    [HumansFact]
    public async Task Grandfathered_attribute_with_different_ruleId_does_not_downgrade()
    {
        var source = Stubs + """

            namespace Humans.Application.Architecture
            {
                [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = true)]
                public sealed class GrandfatheredAttribute : System.Attribute
                {
                    public GrandfatheredAttribute(string ruleId, string justification, string since, string issueRef) { }
                }
            }

            namespace Humans.Application.Services.Teams
            {
                [Humans.Application.Architecture.Grandfathered("HUM9999", "test", "2026-05-15", "test")]
                public sealed class CampService : Humans.Application.Interfaces.Camps.ICampService { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ApplicationServiceLocationAnalyzer(),
            "Humans.Application",
            source);

        var hum0012 = diagnostics.Where(IsHum0012).ToList();
        hum0012.Should().ContainSingle();
        hum0012[0].Severity.Should().Be(Microsoft.CodeAnalysis.DiagnosticSeverity.Error);
    }

    [HumansFact]
    public async Task Does_not_fire_outside_Application_assembly()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Teams
            {
                public sealed class CampService : Humans.Application.Interfaces.Camps.ICampService { }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ApplicationServiceLocationAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Should().BeEmpty();
    }
}
