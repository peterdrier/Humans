using AwesomeAssertions;

namespace Humans.Analyzers.Tests;

public class CrossSectionRepositoryInjectionAnalyzerTests
{
    private const string Stubs = """
        namespace Humans.Domain.Attributes
        {
            [System.AttributeUsage(System.AttributeTargets.Interface | System.AttributeTargets.Class)]
            public sealed class SectionAttribute : System.Attribute
            {
                public SectionAttribute(string name) { Name = name; }
                public string Name { get; }
            }
        }

        namespace Humans.Application.Interfaces
        {
            public interface IApplicationService { }
        }

        namespace Humans.Application.Interfaces.Repositories
        {
            public interface IRepository { }

            [Humans.Domain.Attributes.Section("Camps")]
            public interface ICampRepository : IRepository { }

            [Humans.Domain.Attributes.Section("Teams")]
            public interface ITeamRepository : IRepository { }

            public interface IUnmarkedRepository : IRepository { }
        }

        namespace Humans.Application.Interfaces.Camps
        {
            public interface ICampService : Humans.Application.Interfaces.IApplicationService { }
        }
        """;

    private static bool IsHum0017(Microsoft.CodeAnalysis.Diagnostic d) =>
        string.Equals(d.Id, CrossSectionRepositoryInjectionAnalyzer.DiagnosticId, StringComparison.Ordinal);

    private static bool IsHum0018(Microsoft.CodeAnalysis.Diagnostic d) =>
        string.Equals(d.Id, CrossSectionRepositoryInjectionAnalyzer.IndeterminateSectionId, StringComparison.Ordinal);

    [HumansFact]
    public async Task Fires_when_service_injects_foreign_section_repository()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Camps
            {
                public sealed class CampService : Humans.Application.Interfaces.Camps.ICampService
                {
                    public CampService(Humans.Application.Interfaces.Repositories.ITeamRepository teams) { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new CrossSectionRepositoryInjectionAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0017(d));
    }

    [HumansFact]
    public async Task Does_not_fire_when_service_injects_own_section_repository()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Camps
            {
                public sealed class CampService : Humans.Application.Interfaces.Camps.ICampService
                {
                    public CampService(Humans.Application.Interfaces.Repositories.ICampRepository camps) { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new CrossSectionRepositoryInjectionAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(d => IsHum0017(d)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_when_parameter_is_not_a_repository()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Camps
            {
                public sealed class CampService : Humans.Application.Interfaces.Camps.ICampService
                {
                    // ICampService is marked [Section("Camps")]-free but lives in Camps;
                    // it's an IApplicationService, not an IRepository — must be ignored.
                    public CampService(Humans.Application.Interfaces.Camps.ICampService other) { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new CrossSectionRepositoryInjectionAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(d => IsHum0017(d)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Fires_HUM0018_for_unmarked_repository()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Camps
            {
                public sealed class CampService : Humans.Application.Interfaces.Camps.ICampService
                {
                    public CampService(Humans.Application.Interfaces.Repositories.IUnmarkedRepository repo) { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new CrossSectionRepositoryInjectionAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(d => IsHum0017(d)).Should().BeEmpty();
        diagnostics.Should().ContainSingle(d => IsHum0018(d));
    }

    [HumansFact]
    public async Task Does_not_fire_HUM0018_for_non_repository_parameter()
    {
        // HUM0018 only fires when HUM0017 is checking an IRepository parameter
        // and can't resolve its section. Non-repository deps (loggers, options,
        // sibling services) are out of scope entirely.
        var source = Stubs + """

            namespace Humans.Application.Services.Camps
            {
                public sealed class CampService : Humans.Application.Interfaces.Camps.ICampService
                {
                    public CampService(System.IServiceProvider sp) { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new CrossSectionRepositoryInjectionAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(d => IsHum0017(d) || IsHum0018(d)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_when_host_class_is_not_an_IApplicationService()
    {
        // A helper/builder in Services.Camps that does NOT implement IApplicationService
        // is out of scope — the rule targets the service surface, not internal helpers.
        var source = Stubs + """

            namespace Humans.Application.Services.Camps
            {
                public sealed class CampHelper
                {
                    public CampHelper(Humans.Application.Interfaces.Repositories.ITeamRepository teams) { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new CrossSectionRepositoryInjectionAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(d => IsHum0017(d)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_outside_Application_assembly()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Camps
            {
                public sealed class CampService : Humans.Application.Interfaces.Camps.ICampService
                {
                    public CampService(Humans.Application.Interfaces.Repositories.ITeamRepository teams) { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new CrossSectionRepositoryInjectionAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Where(d => IsHum0017(d)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Default_severity_is_warning()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Camps
            {
                public sealed class CampService : Humans.Application.Interfaces.Camps.ICampService
                {
                    public CampService(Humans.Application.Interfaces.Repositories.ITeamRepository teams) { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new CrossSectionRepositoryInjectionAnalyzer(),
            "Humans.Application",
            source);

        var hum0017 = diagnostics.Where(d => IsHum0017(d)).ToList();
        hum0017.Should().ContainSingle();
        hum0017[0].Severity.Should().Be(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);
    }
}
