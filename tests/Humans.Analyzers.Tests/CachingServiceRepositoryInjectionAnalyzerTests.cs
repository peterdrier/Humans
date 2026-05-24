using AwesomeAssertions;
using Microsoft.CodeAnalysis;

namespace Humans.Analyzers.Tests;

public class CachingServiceRepositoryInjectionAnalyzerTests
{
    private const string Stubs = """
        namespace Humans.Application.Interfaces.Repositories
        {
            public interface IRepository { }
            public interface ICampRepository : IRepository { }
            public interface IDeepRepository : ICampRepository { }
        }

        namespace Humans.Application.Interfaces.Camps
        {
            public interface ICampService { }
        }

        namespace Microsoft.Extensions.DependencyInjection
        {
            public interface IServiceScopeFactory { }
        }
        """;

    private static bool IsHum0020(Diagnostic d) =>
        string.Equals(d.Id, CachingServiceRepositoryInjectionAnalyzer.DiagnosticId, StringComparison.Ordinal);

    [HumansFact]
    public async Task Fires_when_caching_service_injects_repository()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Services.Camps
            {
                public sealed class CachingCampService
                {
                    public CachingCampService(Humans.Application.Interfaces.Repositories.ICampRepository repo) { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new CachingServiceRepositoryInjectionAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0020(d));
    }

    [HumansFact]
    public async Task Fires_on_indirect_repository_extension()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Services.Camps
            {
                public sealed class CachingCampService
                {
                    public CachingCampService(Humans.Application.Interfaces.Repositories.IDeepRepository repo) { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new CachingServiceRepositoryInjectionAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0020(d));
    }

    [HumansFact]
    public async Task Does_not_fire_when_caching_service_uses_inner_and_cache_plumbing()
    {
        var source = Stubs + """

            namespace Humans.Infrastructure.Services.Camps
            {
                public sealed class CachingCampService
                {
                    public CachingCampService(
                        Humans.Application.Interfaces.Camps.ICampService inner,
                        Microsoft.Extensions.DependencyInjection.IServiceScopeFactory scopeFactory) { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new CachingServiceRepositoryInjectionAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Where(IsHum0020).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_for_non_caching_service()
    {
        var source = Stubs + """

            namespace Humans.Application.Services.Camps
            {
                public sealed class CampService
                {
                    public CampService(Humans.Application.Interfaces.Repositories.ICampRepository repo) { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new CachingServiceRepositoryInjectionAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0020).Should().BeEmpty();
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

            namespace Humans.Infrastructure.Services.Camps
            {
                [Humans.Application.Architecture.Grandfathered("HUM0020", "test", "2026-05-24", "test")]
                public sealed class CachingCampService
                {
                    public CachingCampService(Humans.Application.Interfaces.Repositories.ICampRepository repo) { }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new CachingServiceRepositoryInjectionAnalyzer(),
            "Humans.Infrastructure",
            source);

        var hum0020 = diagnostics.Where(IsHum0020).ToList();
        hum0020.Should().ContainSingle();
        hum0020[0].Severity.Should().Be(DiagnosticSeverity.Warning);
    }
}
