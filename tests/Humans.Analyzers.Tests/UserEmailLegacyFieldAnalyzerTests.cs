using AwesomeAssertions;

namespace Humans.Analyzers.Tests;

public class UserEmailLegacyFieldAnalyzerTests
{
    private const string DomainStub = """
        namespace Humans.Users.Contracts
        {
            public class UserEmail
            {
                public bool IsOAuth { get; set; }
                public int DisplayOrder { get; set; }
                public string? Provider { get; set; }
            }

            public class User
            {
                public string? GoogleEmail { get; set; }
                public string? GetGoogleServiceEmail() => null;
            }
        }
        """;

    private static bool IsHum0001(Microsoft.CodeAnalysis.Diagnostic d) =>
        string.Equals(d.Id, UserEmailLegacyFieldAnalyzer.DiagnosticId, StringComparison.Ordinal);

    [HumansFact]
    public async Task Analyze_IsOAuthRead_Fires()
    {
        var source = DomainStub + """

            namespace Some.App.Code
            {
                public class Reader
                {
                    public bool Check(Humans.Users.Contracts.UserEmail email) => email.IsOAuth;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new UserEmailLegacyFieldAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0001(d));
    }

    [HumansFact]
    public async Task Analyze_DisplayOrderInitializer_Fires()
    {
        var source = DomainStub + """

            namespace Some.Web.Code
            {
                public class Writer
                {
                    public Humans.Users.Contracts.UserEmail Build() =>
                        new Humans.Users.Contracts.UserEmail { DisplayOrder = 1 };
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new UserEmailLegacyFieldAnalyzer(),
            "Humans.Web",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0001(d));
    }

    [HumansFact]
    public async Task Analyze_GoogleEmailAssignment_Fires()
    {
        var source = DomainStub + """

            namespace Some.App.Code
            {
                public class Writer
                {
                    public void Set(Humans.Users.Contracts.User user) => user.GoogleEmail = "x@y";
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new UserEmailLegacyFieldAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0001(d));
    }

    [HumansFact]
    public async Task Analyze_GetGoogleServiceEmailCall_Fires()
    {
        var source = DomainStub + """

            namespace Some.App.Code
            {
                public class Reader
                {
                    public string? Get(Humans.Users.Contracts.User user) => user.GetGoogleServiceEmail();
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new UserEmailLegacyFieldAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Should().ContainSingle(d => IsHum0001(d));
    }

    [HumansFact]
    public async Task Analyze_SameNameOtherType_IsSilent()
    {
        var source = """
            namespace Some.Other
            {
                public class Foo
                {
                    public bool IsOAuth { get; set; }
                }
            }

            namespace Some.App.Code
            {
                public class Reader
                {
                    public bool Check(Some.Other.Foo foo) => foo.IsOAuth;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new UserEmailLegacyFieldAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0001).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Analyze_ProviderNotNull_IsSilent()
    {
        var source = DomainStub + """

            namespace Some.App.Code
            {
                public class Reader
                {
                    public bool IsOAuth(Humans.Users.Contracts.UserEmail email) => email.Provider != null;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new UserEmailLegacyFieldAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(IsHum0001).Should().BeEmpty();
    }
}
