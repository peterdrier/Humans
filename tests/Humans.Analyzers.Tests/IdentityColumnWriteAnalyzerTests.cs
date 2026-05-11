using System.Threading.Tasks;
using AwesomeAssertions;
using Humans.Testing;

namespace Humans.Analyzers.Tests;

public class IdentityColumnWriteAnalyzerTests
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
        }
        """;

    private static bool IsHum0002(Microsoft.CodeAnalysis.Diagnostic d) =>
        string.Equals(d.Id, IdentityColumnWriteAnalyzer.DiagnosticId, System.StringComparison.Ordinal);

    [HumansFact]
    public async Task Fires_on_User_Email_direct_assignment_in_Application()
    {
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
            new IdentityColumnWriteAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0002(d));
    }

    [HumansFact]
    public async Task Fires_on_object_initializer_in_Web()
    {
        var source = DomainStub + """

            namespace Some.Web.Code
            {
                public class Writer
                {
                    public Humans.Domain.Entities.User Build() =>
                        new Humans.Domain.Entities.User { NormalizedEmail = "X" };
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new IdentityColumnWriteAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0002(d));
    }

    [HumansFact]
    public async Task Fires_on_all_five_forbidden_setters()
    {
        var source = DomainStub + """

            namespace Some.App.Code
            {
                public class Writer
                {
                    public void Set(Humans.Domain.Entities.User u)
                    {
                        u.Email = "a";
                        u.NormalizedEmail = "B";
                        u.EmailConfirmed = true;
                        u.UserName = "n";
                        u.NormalizedUserName = "N";
                    }
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new IdentityColumnWriteAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().HaveCount(5).And.AllSatisfy(d => IsHum0002(d).Should().BeTrue());
    }

    [HumansFact]
    public async Task Does_not_fire_outside_Application_or_Web()
    {
        var source = DomainStub + """

            namespace Some.Infra.Code
            {
                public class Writer
                {
                    public void Set(Humans.Domain.Entities.User u) => u.Email = "x";
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new IdentityColumnWriteAnalyzer(),
            "Humans.Infrastructure",
            source);

        diagnostics.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_on_non_Identity_property_on_User()
    {
        var source = DomainStub + """

            namespace Some.App.Code
            {
                public class Writer
                {
                    public void Set(Humans.Domain.Entities.User u) => u.OtherField = "ok";
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new IdentityColumnWriteAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Fires_on_compound_assignment_plus_equals()
    {
        var source = DomainStub + """

            namespace Some.App.Code
            {
                public class Writer
                {
                    public void Set(Humans.Domain.Entities.User user) => user.Email += "@y";
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new IdentityColumnWriteAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0002(d));
    }

    [HumansFact]
    public async Task Fires_on_coalesce_assignment_question_question_equals()
    {
        var source = DomainStub + """

            namespace Some.App.Code
            {
                public class Writer
                {
                    public void Set(Humans.Domain.Entities.User user) => user.NormalizedEmail ??= "X";
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new IdentityColumnWriteAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0002(d));
    }

    [HumansFact]
    public async Task Fires_on_compound_or_equals_on_bool()
    {
        var source = DomainStub + """

            namespace Some.App.Code
            {
                public class Writer
                {
                    public void Set(Humans.Domain.Entities.User user) => user.EmailConfirmed |= true;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new IdentityColumnWriteAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0002(d));
    }

    [HumansFact]
    public async Task Does_not_fire_on_property_read()
    {
        var source = DomainStub + """

            namespace Some.App.Code
            {
                public class Reader
                {
                    public string? Get(Humans.Domain.Entities.User u) => u.Email;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new IdentityColumnWriteAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().BeEmpty();
    }
}
