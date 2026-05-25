using AwesomeAssertions;

namespace Humans.Analyzers.Tests;

public class ObsoleteCrossDomainNavReadAnalyzerTests
{
    private const string DomainStub = """
        using System;

        namespace Humans.Domain.Entities
        {
            public class User { public string? Email { get; set; } }
            public class Team { public string Name { get; set; } = ""; }

            public class TeamMember
            {
                public Guid UserId { get; set; }

                [Obsolete("Cross-domain nav; resolve via IUserService instead.")]
                public User User { get; set; } = new User();

                public Team Team { get; set; } = new Team();
            }
        }
        """;

    private static bool IsHum0021(Microsoft.CodeAnalysis.Diagnostic d) =>
        string.Equals(d.Id, ObsoleteCrossDomainNavReadAnalyzer.DiagnosticId, StringComparison.Ordinal);

    [HumansFact]
    public async Task Fires_on_obsolete_cross_domain_nav_read_in_application()
    {
        var source = DomainStub + """

            namespace Humans.Application.Services.Teams
            {
                public class Reader
                {
                    public string? Get(Humans.Domain.Entities.TeamMember member) =>
                        member.User.Email;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ObsoleteCrossDomainNavReadAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0021(d));
    }

    [HumansFact]
    public async Task Fires_on_obsolete_cross_domain_nav_read_in_web()
    {
        var source = DomainStub + """

            namespace Humans.Web.Controllers
            {
                public class Reader
                {
                    public object Get(Humans.Domain.Entities.TeamMember member) =>
                        member.User;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ObsoleteCrossDomainNavReadAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0021(d));
    }

    [HumansFact]
    public async Task Does_not_fire_on_same_named_non_obsolete_property()
    {
        var source = DomainStub + """

            namespace Humans.Application.Services.Teams
            {
                public class Reader
                {
                    public string Get(Humans.Domain.Entities.TeamMember member) =>
                        member.Team.Name;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ObsoleteCrossDomainNavReadAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_on_unrelated_HttpContext_User_property()
    {
        var source = DomainStub + """

            namespace Microsoft.AspNetCore.Http
            {
                public class HttpContext
                {
                    public System.Security.Claims.ClaimsPrincipal User { get; set; } = new();
                }
            }

            namespace Humans.Web.Authorization
            {
                public class Reader
                {
                    public object Get(Microsoft.AspNetCore.Http.HttpContext context) =>
                        context.User;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ObsoleteCrossDomainNavReadAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_on_assignment_target()
    {
        var source = DomainStub + """

            namespace Humans.Application.Services.Teams
            {
                public class Writer
                {
                    public void Set(Humans.Domain.Entities.TeamMember member, Humans.Domain.Entities.User user) =>
                        member.User = user;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ObsoleteCrossDomainNavReadAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_inside_nameof()
    {
        var source = DomainStub + """

            namespace Humans.Application.Services.Teams
            {
                public class Reader
                {
                    public string Get() => nameof(Humans.Domain.Entities.TeamMember.User);
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ObsoleteCrossDomainNavReadAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_in_infrastructure_ef_configurations()
    {
        var source = DomainStub + """

            namespace Humans.Infrastructure.Data.Configurations.Teams
            {
                public class TeamMemberConfiguration
                {
                    public object Configure(Humans.Domain.Entities.TeamMember member) =>
                        member.User;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ObsoleteCrossDomainNavReadAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_outside_application_web_or_infrastructure()
    {
        var source = DomainStub + """

            namespace Humans.Tooling
            {
                public class Reader
                {
                    public object Get(Humans.Domain.Entities.TeamMember member) => member.User;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new ObsoleteCrossDomainNavReadAnalyzer(),
            "Humans.Tooling",
            source);

        diagnostics.Should().BeEmpty();
    }
}
