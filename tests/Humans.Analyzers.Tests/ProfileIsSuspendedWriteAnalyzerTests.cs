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

            namespace Humans.Application.Services.Profiles
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
    public async Task Fires_on_write_from_arbitrary_Web_type()
    {
        // Positive scope test for Web — completes the canary triangle with the
        // existing Application + Infrastructure positive tests. A scope change
        // that dropped Web (e.g. ApplicationOrInfrastructure) would otherwise
        // pass every remaining test silently.
        var source = DomainStub + """

            namespace Humans.Web.Controllers
            {
                public class SomeController
                {
                    public void Suspend(Humans.Domain.Entities.Profile p) => p.IsSuspended = true;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ProfileIsSuspendedWriteAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0004(d));
    }

    [HumansFact]
    public async Task Fires_on_write_from_arbitrary_Infrastructure_type()
    {
        // Positive scope test for Infrastructure — same canary purpose as the
        // matching HUM0006 test in EmailMutationPathsAnalyzerTests. Without
        // this, a scope narrowing from ApplicationWebOrInfrastructure to
        // ApplicationOrWeb would pass every remaining Application/Web test.
        var source = DomainStub + """

            namespace Humans.Infrastructure.Repositories.Something
            {
                public class SomethingElse
                {
                    public void Suspend(Humans.Domain.Entities.Profile p) => p.IsSuspended = true;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ProfileIsSuspendedWriteAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0004(d));
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
