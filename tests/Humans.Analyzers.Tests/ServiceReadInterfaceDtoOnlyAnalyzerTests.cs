using AwesomeAssertions;
using Microsoft.CodeAnalysis;

namespace Humans.Analyzers.Tests;

public class ServiceReadInterfaceDtoOnlyAnalyzerTests
{
    // Stub the section-owned DTO, an EF entity, a DbSet, and the
    // GrandfatheredAttribute. IQueryable<T> comes from the real BCL via the
    // harness's trusted-platform-assemblies reference list.
    private const string Stubs = """
        namespace Humans.Application.DTOs
        {
            public sealed record CampInfo(System.Guid Id, string Name);
        }

        namespace Humans.Domain.Entities
        {
            public sealed class Camp
            {
                public System.Guid Id { get; init; }
            }
        }

        namespace Microsoft.EntityFrameworkCore
        {
            public class DbSet<T> { }
        }

        namespace Humans.Application.Architecture
        {
            [System.AttributeUsage(System.AttributeTargets.Class | System.AttributeTargets.Interface, AllowMultiple = true, Inherited = false)]
            public sealed class GrandfatheredAttribute : System.Attribute
            {
                public GrandfatheredAttribute(string ruleId, string justification, string since, string issueRef) { }
            }
        }
        """;

    private static bool IsHum0029(Diagnostic d) =>
        string.Equals(d.Id, ServiceReadInterfaceDtoOnlyAnalyzer.DiagnosticId, StringComparison.Ordinal);

    [HumansFact]
    public async Task Does_not_fire_on_clean_Read_interface_returning_DTO()
    {
        var source = Stubs + """

            namespace Humans.Application.Interfaces.Camps
            {
                public interface ICampServiceRead
                {
                    System.Threading.Tasks.Task<Humans.Application.DTOs.CampInfo?> GetByIdAsync(System.Guid id);
                    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<Humans.Application.DTOs.CampInfo>> ListAsync();
                    System.Threading.Tasks.Task<int> CountAsync();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ServiceReadInterfaceDtoOnlyAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0029).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Fires_when_Read_interface_returns_entity_directly()
    {
        var source = Stubs + """

            namespace Humans.Application.Interfaces.Camps
            {
                public interface ICampServiceRead
                {
                    System.Threading.Tasks.Task<Humans.Domain.Entities.Camp?> GetAsync(System.Guid id);
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ServiceReadInterfaceDtoOnlyAnalyzer(),
            "Humans.Application",
            source);

        var hits = diagnostics.Where(IsHum0029).ToList();
        hits.Should().ContainSingle();
        hits[0].Severity.Should().Be(DiagnosticSeverity.Error);
        hits[0].GetMessage().Should().Contain("Camp");
    }

    [HumansFact]
    public async Task Fires_when_entity_is_nested_in_generic_arguments()
    {
        var source = Stubs + """

            namespace Humans.Application.Interfaces.Camps
            {
                public interface ICampServiceRead
                {
                    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<Humans.Domain.Entities.Camp>> ListAsync();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ServiceReadInterfaceDtoOnlyAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0029).Should().ContainSingle();
    }

    [HumansFact]
    public async Task Fires_when_entity_is_passed_as_parameter()
    {
        var source = Stubs + """

            namespace Humans.Application.Interfaces.Camps
            {
                public interface ICampServiceRead
                {
                    System.Threading.Tasks.Task<int> SummariseAsync(Humans.Domain.Entities.Camp camp);
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ServiceReadInterfaceDtoOnlyAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0029).Should().ContainSingle();
    }

    [HumansFact]
    public async Task Fires_when_Read_interface_returns_DbSet()
    {
        var source = Stubs + """

            namespace Humans.Application.Interfaces.Camps
            {
                public interface ICampServiceRead
                {
                    Microsoft.EntityFrameworkCore.DbSet<Humans.Application.DTOs.CampInfo> Camps { get; }
                    System.Threading.Tasks.Task<Microsoft.EntityFrameworkCore.DbSet<Humans.Application.DTOs.CampInfo>> GetSetAsync();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ServiceReadInterfaceDtoOnlyAnalyzer(),
            "Humans.Application",
            source);

        // Property accessors are MethodKind.PropertyGet — the analyzer scans
        // Ordinary methods only, so the violation is reported on GetSetAsync.
        diagnostics.Where(IsHum0029).Should().ContainSingle();
    }

    [HumansFact]
    public async Task Fires_when_Read_interface_returns_IQueryable()
    {
        var source = Stubs + """

            namespace Humans.Application.Interfaces.Camps
            {
                public interface ICampServiceRead
                {
                    System.Linq.IQueryable<Humans.Application.DTOs.CampInfo> Query();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ServiceReadInterfaceDtoOnlyAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0029).Should().ContainSingle();
    }

    [HumansFact]
    public async Task Reports_one_diagnostic_per_offending_method()
    {
        var source = Stubs + """

            namespace Humans.Application.Interfaces.Camps
            {
                public interface ICampServiceRead
                {
                    System.Threading.Tasks.Task<Humans.Domain.Entities.Camp?> GetAsync(System.Guid id);
                    System.Threading.Tasks.Task<Humans.Application.DTOs.CampInfo?> GetInfoAsync(System.Guid id);
                    System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<Humans.Domain.Entities.Camp>> ListAsync();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ServiceReadInterfaceDtoOnlyAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0029).Should().HaveCount(2);
    }

    [HumansFact]
    public async Task Does_not_fire_on_non_Read_interface_even_when_exposing_entities()
    {
        var source = Stubs + """

            namespace Humans.Application.Interfaces.Camps
            {
                public interface ICampService
                {
                    System.Threading.Tasks.Task<Humans.Domain.Entities.Camp> CreateAsync(string name);
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ServiceReadInterfaceDtoOnlyAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0029).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_outside_Application_assembly()
    {
        var source = Stubs + """

            namespace Humans.Application.Interfaces.Camps
            {
                public interface ICampServiceRead
                {
                    System.Threading.Tasks.Task<Humans.Domain.Entities.Camp?> GetAsync(System.Guid id);
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ServiceReadInterfaceDtoOnlyAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Where(IsHum0029).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Grandfathered_interface_downgrades_to_warning()
    {
        var source = Stubs + """

            namespace Humans.Application.Interfaces.Camps
            {
                [Humans.Application.Architecture.Grandfathered(
                    ruleId: "HUM0029",
                    justification: "Pre-existing entity leak awaiting projection extraction.",
                    since: "2026-05-28",
                    issueRef: "peterdrier/Humans#000")]
                public interface ICampServiceRead
                {
                    System.Threading.Tasks.Task<Humans.Domain.Entities.Camp?> GetAsync(System.Guid id);
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ServiceReadInterfaceDtoOnlyAnalyzer(),
            "Humans.Application",
            source);

        var hits = diagnostics.Where(IsHum0029).ToList();
        hits.Should().ContainSingle();
        hits[0].Severity.Should().Be(DiagnosticSeverity.Warning);
    }

    [HumansFact]
    public async Task Grandfathered_for_a_different_rule_still_fires_error()
    {
        var source = Stubs + """

            namespace Humans.Application.Interfaces.Camps
            {
                [Humans.Application.Architecture.Grandfathered(
                    ruleId: "HUM0042",
                    justification: "Different rule.",
                    since: "2026-05-28",
                    issueRef: "peterdrier/Humans#000")]
                public interface ICampServiceRead
                {
                    System.Threading.Tasks.Task<Humans.Domain.Entities.Camp?> GetAsync(System.Guid id);
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ServiceReadInterfaceDtoOnlyAnalyzer(),
            "Humans.Application",
            source);

        var hits = diagnostics.Where(IsHum0029).ToList();
        hits.Should().ContainSingle();
        hits[0].Severity.Should().Be(DiagnosticSeverity.Error);
    }

    [HumansFact]
    public async Task Ignores_interface_named_just_IRead()
    {
        // Length filter: bare "IRead" is too short to be a *<Foo>Read pattern.
        var source = Stubs + """

            namespace Humans.Application.Interfaces
            {
                public interface IRead
                {
                    System.Threading.Tasks.Task<Humans.Domain.Entities.Camp?> GetAsync(System.Guid id);
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ServiceReadInterfaceDtoOnlyAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0029).Should().BeEmpty();
    }
}
