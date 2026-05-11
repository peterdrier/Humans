using System.Threading.Tasks;
using AwesomeAssertions;
using Humans.Testing;

namespace Humans.Analyzers.Tests;

public class ProfileIsSuspendedWriteAnalyzerTests
{
    private const string DomainStub = """
        namespace Humans.Domain.Entities
        {
            public class Profile
            {
                public bool IsSuspended { get; set; }
                public string? State { get; set; }
            }
        }
        """;

    private static bool IsHum0004(Microsoft.CodeAnalysis.Diagnostic d) =>
        string.Equals(d.Id, ProfileIsSuspendedWriteAnalyzer.DiagnosticId, System.StringComparison.Ordinal);

    [HumansFact]
    public async Task Fires_on_write_from_arbitrary_application_type()
    {
        var source = DomainStub + """

            namespace Humans.Application.Services.Other
            {
                public class SomethingElse
                {
                    public void Suspend(Humans.Domain.Entities.Profile p) => p.IsSuspended = true;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ProfileIsSuspendedWriteAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0004(d));
    }

    [HumansFact]
    public async Task Does_not_fire_in_allowlisted_ProfileService()
    {
        var source = DomainStub + """

            namespace Humans.Application.Services.Profile
            {
                public class ProfileService
                {
                    public void Suspend(Humans.Domain.Entities.Profile p) => p.IsSuspended = true;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ProfileIsSuspendedWriteAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_in_allowlisted_ProfileRepository_in_Infrastructure()
    {
        var source = DomainStub + """

            namespace Humans.Infrastructure.Repositories.Profiles
            {
                public class ProfileRepository
                {
                    public void Suspend(Humans.Domain.Entities.Profile p) => p.IsSuspended = true;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ProfileIsSuspendedWriteAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Fires_on_compound_or_equals_assignment_outside_allowlist()
    {
        var source = DomainStub + """

            namespace Humans.Application.Services.Other
            {
                public class Sneaky
                {
                    public void Suspend(Humans.Domain.Entities.Profile p) => p.IsSuspended |= true;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ProfileIsSuspendedWriteAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0004(d));
    }

    [HumansFact]
    public async Task Does_not_fire_on_read()
    {
        var source = DomainStub + """

            namespace Humans.Application.Services.Other
            {
                public class Reader
                {
                    public bool IsSuspended(Humans.Domain.Entities.Profile p) => p.IsSuspended;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ProfileIsSuspendedWriteAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().BeEmpty();
    }
}
