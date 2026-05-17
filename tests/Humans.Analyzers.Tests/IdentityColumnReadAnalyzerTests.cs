using AwesomeAssertions;

namespace Humans.Analyzers.Tests;

public class IdentityColumnReadAnalyzerTests
{
    private const string DomainStub = """
        namespace Humans.Domain.Entities
        {
            public class User
            {
                public virtual string? Email { get; set; }
                public virtual string? NormalizedEmail { get; set; }
                public virtual bool EmailConfirmed { get; set; }
                public virtual string? UserName { get; set; }
                public virtual string? NormalizedUserName { get; set; }
                public string? OtherField { get; set; }
            }

            public class UserInfo
            {
                // Same shape as the real DTO: a property called `Email`.
                public string? Email { get; set; }
            }
        }
        """;

    private static bool IsHum0019(Microsoft.CodeAnalysis.Diagnostic d) =>
        string.Equals(d.Id, IdentityColumnReadAnalyzer.DiagnosticId, StringComparison.Ordinal);

    [HumansFact]
    public async Task Fires_on_User_Email_read_in_Application()
    {
        var source = DomainStub + """

            namespace Some.App.Code
            {
                public class Reader
                {
                    public string? Get(Humans.Domain.Entities.User user) => user.Email;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new IdentityColumnReadAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0019(d));
    }

    [HumansFact]
    public async Task Fires_on_User_NormalizedEmail_read_in_Web()
    {
        var source = DomainStub + """

            namespace Some.Web.Code
            {
                public class Reader
                {
                    public string? Get(Humans.Domain.Entities.User user) => user.NormalizedEmail;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new IdentityColumnReadAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0019(d));
    }

    [HumansFact]
    public async Task Fires_on_all_four_forbidden_getters()
    {
        var source = DomainStub + """

            namespace Some.App.Code
            {
                public class Reader
                {
                    public void Read(Humans.Domain.Entities.User u)
                    {
                        _ = u.Email;
                        _ = u.NormalizedEmail;
                        _ = u.UserName;
                        _ = u.NormalizedUserName;
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new IdentityColumnReadAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().HaveCount(4).And.AllSatisfy(d => IsHum0019(d).Should().BeTrue());
    }

    [HumansFact]
    public async Task Does_not_fire_outside_Application_or_Web()
    {
        var source = DomainStub + """

            namespace Some.Infra.Code
            {
                public class Reader
                {
                    public string? Get(Humans.Domain.Entities.User u) => u.Email;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new IdentityColumnReadAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_on_UserInfo_Email_read()
    {
        // UserInfo (DTO) has its own `Email` property — reads of that are
        // the CORRECT path post-#506. Analyzer must not flag them.
        var source = DomainStub + """

            namespace Some.App.Code
            {
                public class Reader
                {
                    public string? Get(Humans.Domain.Entities.UserInfo info) => info.Email;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new IdentityColumnReadAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_on_non_Identity_property_on_User()
    {
        var source = DomainStub + """

            namespace Some.App.Code
            {
                public class Reader
                {
                    public string? Get(Humans.Domain.Entities.User u) => u.OtherField;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new IdentityColumnReadAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_on_assignment_target()
    {
        // Writes are HUM0002's job — HUM0019 stays out of the way.
        var source = DomainStub + """

            namespace Some.App.Code
            {
                public class Writer
                {
                    public void Set(Humans.Domain.Entities.User user) => user.Email = "x@y";
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new IdentityColumnReadAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Fires_on_property_pattern_read()
    {
        // Property patterns (`u is { Email: not null }`) fire IPropertyReferenceOperation
        // because IPropertySubpatternOperation.Member wraps a PropertyReference child —
        // registering only PropertyReference catches this path without double-counting.
        // This test is regression coverage: the pattern is NOT a bypass.
        var source = DomainStub + """

            namespace Some.App.Code
            {
                public class Reader
                {
                    public bool HasEmail(Humans.Domain.Entities.User u) =>
                        u is { Email: not null };
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new IdentityColumnReadAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0019(d));
    }

    [HumansFact]
    public async Task Fires_on_switch_property_pattern_read()
    {
        // Same path via `switch` expression property pattern — also caught by
        // IPropertyReferenceOperation, not a bypass. Regression coverage.
        var source = DomainStub + """

            namespace Some.App.Code
            {
                public class Reader
                {
                    public string Classify(Humans.Domain.Entities.User u) => u switch
                    {
                        { UserName: null } => "anon",
                        _ => "named"
                    };
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new IdentityColumnReadAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0019(d));
    }

    [HumansFact]
    public async Task Fires_on_string_interpolation_read()
    {
        var source = DomainStub + """

            namespace Some.App.Code
            {
                public class Reader
                {
                    public string Get(Humans.Domain.Entities.User u) => $"{u.UserName}";
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new IdentityColumnReadAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0019(d));
    }
}
