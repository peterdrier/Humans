using AwesomeAssertions;

namespace Humans.Analyzers.Tests;

public class IdentityColumnWriteAnalyzerTests
{
    private const string DomainStub = """
        namespace Humans.Users.Contracts
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
        string.Equals(d.Id, IdentityColumnWriteAnalyzer.DiagnosticId, StringComparison.Ordinal);

    [HumansFact]
    public async Task Analyze_UserEmailAssignment_Fires()
    {
        var source = DomainStub + """

            namespace Some.App.Code
            {
                public class Writer
                {
                    public void Set(Humans.Users.Contracts.User user) => user.Email = "x@y";
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
    public async Task Analyze_ObjectInitializerInWeb_Fires()
    {
        var source = DomainStub + """

            namespace Some.Web.Code
            {
                public class Writer
                {
                    public Humans.Users.Contracts.User Build() =>
                        new Humans.Users.Contracts.User { NormalizedEmail = "X" };
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
    public async Task Analyze_ForbiddenSetters_AllFire()
    {
        var source = DomainStub + """

            namespace Some.App.Code
            {
                public class Writer
                {
                    public void Set(Humans.Users.Contracts.User u)
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
    public async Task Analyze_NonIdentityProperty_IsSilent()
    {
        var source = DomainStub + """

            namespace Some.App.Code
            {
                public class Writer
                {
                    public void Set(Humans.Users.Contracts.User u) => u.OtherField = "ok";
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
    public async Task Analyze_PlusEqualsAssignment_Fires()
    {
        var source = DomainStub + """

            namespace Some.App.Code
            {
                public class Writer
                {
                    public void Set(Humans.Users.Contracts.User user) => user.Email += "@y";
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
    public async Task Analyze_CoalesceAssignment_Fires()
    {
        var source = DomainStub + """

            namespace Some.App.Code
            {
                public class Writer
                {
                    public void Set(Humans.Users.Contracts.User user) => user.NormalizedEmail ??= "X";
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
    public async Task Analyze_OrEqualsAssignment_Fires()
    {
        var source = DomainStub + """

            namespace Some.App.Code
            {
                public class Writer
                {
                    public void Set(Humans.Users.Contracts.User user) => user.EmailConfirmed |= true;
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
    public async Task Analyze_PropertyRead_IsSilent()
    {
        var source = DomainStub + """

            namespace Some.App.Code
            {
                public class Reader
                {
                    public string? Get(Humans.Users.Contracts.User u) => u.Email;
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
