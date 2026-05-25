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

        diagnostics.Where(IsHum0017).Should().BeEmpty();
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

        diagnostics.Where(IsHum0017).Should().BeEmpty();
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

        diagnostics.Where(IsHum0017).Should().BeEmpty();
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

        diagnostics.Where(IsHum0017).Should().BeEmpty();
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

        diagnostics.Where(IsHum0017).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Users_and_profiles_fold_to_one_section_for_intra_section_injection()
    {
        // The arch-test ServiceBoundaryArchitectureTests.ServiceSection folds
        // Users + Profile + Profiles to "Humans" (one ownership section). The
        // analyzer must apply the same fold so that a Services.Users service
        // injecting a [Section("Humans")]-tagged repo (e.g. IUserEmailRepository)
        // is not flagged as cross-section.
        var source = """
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

                [Humans.Domain.Attributes.Section("Humans")]
                public interface IUserRepository : IRepository { }

                [Humans.Domain.Attributes.Section("Humans")]
                public interface IUserEmailRepository : IRepository { }

                [Humans.Domain.Attributes.Section("Humans")]
                public interface IProfileRepository : IRepository { }
            }

            namespace Humans.Application.Interfaces.Users
            {
                public interface IUserService : Humans.Application.Interfaces.IApplicationService { }
            }

            namespace Humans.Application.Services.Users
            {
                public sealed class UserService : Humans.Application.Interfaces.Users.IUserService
                {
                    public UserService(
                        Humans.Application.Interfaces.Repositories.IUserRepository users,
                        Humans.Application.Interfaces.Repositories.IUserEmailRepository emails,
                        Humans.Application.Interfaces.Repositories.IProfileRepository profiles) { }
                }
            }

            namespace Humans.Application.Services.Profiles
            {
                public sealed class ProfileService : Humans.Application.Interfaces.Users.IUserService
                {
                    public ProfileService(
                        Humans.Application.Interfaces.Repositories.IUserRepository users,
                        Humans.Application.Interfaces.Repositories.IProfileRepository profiles) { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new CrossSectionRepositoryInjectionAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0017).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Legacy_section_tag_on_repo_is_folded_to_humans()
    {
        // Defense-in-depth: even if a repository is tagged with the legacy
        // [Section("Profiles")] or [Section("Users")] value (pre-merger), the
        // analyzer's fold treats it as "Humans" so a Services.Users service
        // injecting it is not flagged as cross-section. This keeps the rule
        // stable across the gradual users-profiles-one-section retag rollout.
        var source = """
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

                [Humans.Domain.Attributes.Section("Profiles")]
                public interface IProfileRepository : IRepository { }
            }

            namespace Humans.Application.Interfaces.Users
            {
                public interface IUserService : Humans.Application.Interfaces.IApplicationService { }
            }

            namespace Humans.Application.Services.Users
            {
                public sealed class UserService : Humans.Application.Interfaces.Users.IUserService
                {
                    public UserService(Humans.Application.Interfaces.Repositories.IProfileRepository profiles) { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new CrossSectionRepositoryInjectionAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0017).Should().BeEmpty();
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

        var hum0017 = diagnostics.Where(IsHum0017).ToList();
        hum0017.Should().ContainSingle();
        hum0017[0].Severity.Should().Be(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning);
    }
}
