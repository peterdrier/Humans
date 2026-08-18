using AwesomeAssertions;
using Microsoft.CodeAnalysis;

namespace Humans.Analyzers.Tests;

public class CachingDecoratorRuleTests
{
    private const string Stubs = """
        namespace Humans.Base.Interfaces.Repositories
        {
            public interface IRepository { }
            public interface ITeamRepository : IRepository
            {
                string GetTeam();
            }
        }

        namespace Humans.Base.Attributes
        {
            [System.AttributeUsage(System.AttributeTargets.Class, AllowMultiple = true, Inherited = false)]
            public sealed class GrandfatheredAttribute : System.Attribute
            {
                public GrandfatheredAttribute(string ruleId, string justification, string since, string issueRef) { }
            }
        }
        """;

    // What makes a compilation a section to AssemblyScope.IsSection: the
    // Section : ISection entry point in the assembly's root namespace, exactly as boot
    // discovery finds it. Paired with the "Humans.Teams" assembly name below.
    private const string SectionEntryPoint = """
        namespace Humans.Base.Interfaces
        {
            public interface ISection { }
        }

        namespace Humans.Teams
        {
            public sealed class Section : Humans.Base.Interfaces.ISection { }
        }
        """;

    private static bool IsHum0020(Diagnostic d) =>
        string.Equals(d.Id, "HUM0020", StringComparison.Ordinal);

    [HumansFact]
    public async Task Fires_error_on_repository_constructor_parameter_in_caching_decorator()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Services.Teams
            {
                public sealed class CachingTeamService(
                    Humans.Base.Interfaces.Repositories.ITeamRepository repository)
                {
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Infrastructure",
            source);

        var hits = diagnostics.Where(IsHum0020).ToList();
        hits.Should().ContainSingle();
        hits[0].Severity.Should().Be(DiagnosticSeverity.Error);
    }

    [HumansFact]
    public async Task Fires_on_nested_helper_repository_field_inside_caching_decorator()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Services.Tickets
            {
                public sealed class CachingTicketQueryService
                {
                    private sealed class OrdersCache
                    {
                        private readonly Humans.Base.Interfaces.Repositories.ITeamRepository _repository = null!;
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Where(IsHum0020).Should().ContainSingle();
    }

    [HumansFact]
    public async Task Fires_on_method_parameter_repository_reference()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Services.Teams
            {
                public sealed class CachingTeamService
                {
                    public string Load(Humans.Base.Interfaces.Repositories.ITeamRepository repository)
                    {
                        return repository.GetTeam();
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Where(IsHum0020).Should().ContainSingle();
    }

    [HumansFact]
    public async Task Fires_on_repository_typed_property_in_caching_decorator()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Services.Teams
            {
                public sealed class CachingTeamService
                {
                    public Humans.Base.Interfaces.Repositories.ITeamRepository Repo { get; init; } = null!;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Where(IsHum0020).Should().ContainSingle();
    }

    [HumansFact]
    public async Task Fires_on_repository_return_type_in_caching_decorator()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Services.Teams
            {
                public sealed class CachingTeamService
                {
                    public Humans.Base.Interfaces.Repositories.ITeamRepository GetRepo() => null!;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Where(IsHum0020).Should().ContainSingle();
    }

    [HumansFact]
    public async Task Fires_on_repository_wrapped_in_generic_type()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Services.Teams
            {
                public sealed class CachingTeamService(
                    System.Func<Humans.Base.Interfaces.Repositories.ITeamRepository> repositoryFactory)
                {
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Where(IsHum0020).Should().ContainSingle();
    }

    [HumansFact]
    public async Task Fires_on_repository_constrained_type_parameter()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Services.Teams
            {
                public sealed class CachingTeamService
                {
                    public TRepository GetRepo<TRepository>()
                        where TRepository : Humans.Base.Interfaces.Repositories.IRepository
                        => default!;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Where(IsHum0020).Should().ContainSingle();
    }

    [HumansFact]
    public async Task Does_not_fire_on_regular_infrastructure_service_repository_parameter()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Services.Teams
            {
                public sealed class TeamService(
                    Humans.Base.Interfaces.Repositories.ITeamRepository repository)
                {
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Where(IsHum0020).Should().BeEmpty();
    }

    /// <summary>
    /// The regression test for the namespace filter that silently switched this rule off.
    /// Every one of the 11 caching decorators lives in a section project
    /// (nobodies-collective/Humans#866, G5) under <c>Humans.&lt;Section&gt;.Services</c>, so the
    /// old <c>Humans.Infrastructure.Services</c> namespace requirement matched none of them
    /// while the assembly gate happily admitted their assemblies. Without this test the rule
    /// can die the same way again and stay green doing it.
    /// </summary>
    [HumansFact]
    public async Task Fires_on_repository_in_section_project_caching_decorator()
    {
        var source = SectionEntryPoint + Stubs + """

            namespace Humans.Teams.Services
            {
                public sealed class CachingTeamService(
                    Humans.Base.Interfaces.Repositories.ITeamRepository repository)
                {
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Teams",
            source);

        var hits = diagnostics.Where(IsHum0020).ToList();
        hits.Should().ContainSingle();
        hits[0].Severity.Should().Be(DiagnosticSeverity.Error);
    }

    [HumansFact]
    public async Task Grandfathered_decorator_reports_warning()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Services.Teams
            {
                [Humans.Base.Attributes.Grandfathered(
                    ruleId: "HUM0020",
                    justification: "Existing repository-backed warm path.",
                    since: "2026-05-24",
                    issueRef: "docs/architecture/roslyn-analysis.md#hum0020")]
                public sealed class CachingTeamService(
                    Humans.Base.Interfaces.Repositories.ITeamRepository repository)
                {
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new SectionRulesAnalyzer(),
            "Humans.Infrastructure",
            source);

        var hits = diagnostics.Where(IsHum0020).ToList();
        hits.Should().ContainSingle();
        hits[0].Severity.Should().Be(DiagnosticSeverity.Warning);
    }
}
