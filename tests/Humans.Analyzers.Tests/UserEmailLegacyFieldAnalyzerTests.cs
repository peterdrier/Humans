using System.Linq;
using System.Threading.Tasks;
using AwesomeAssertions;
using Humans.Testing;

namespace Humans.Analyzers.Tests;

public class UserEmailLegacyFieldAnalyzerTests
{
    private const string DomainStub = """
        namespace Humans.Domain.Entities
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
        string.Equals(d.Id, UserEmailLegacyFieldAnalyzer.DiagnosticId, System.StringComparison.Ordinal);

    [HumansFact]
    public async Task Fires_on_UserEmail_IsOAuth_read_in_Application_assembly()
    {
        var source = DomainStub + """

            namespace Some.App.Code
            {
                public class Reader
                {
                    public bool Check(Humans.Domain.Entities.UserEmail email) => email.IsOAuth;
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
    public async Task Fires_on_UserEmail_DisplayOrder_object_initializer_in_Web()
    {
        var source = DomainStub + """

            namespace Some.Web.Code
            {
                public class Writer
                {
                    public Humans.Domain.Entities.UserEmail Build() =>
                        new Humans.Domain.Entities.UserEmail { DisplayOrder = 1 };
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
    public async Task Fires_on_User_GoogleEmail_assignment_in_Application()
    {
        var source = DomainStub + """

            namespace Some.App.Code
            {
                public class Writer
                {
                    public void Set(Humans.Domain.Entities.User user) => user.GoogleEmail = "x@y";
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
    public async Task Fires_on_User_GetGoogleServiceEmail_call_in_Application()
    {
        var source = DomainStub + """

            namespace Some.App.Code
            {
                public class Reader
                {
                    public string? Get(Humans.Domain.Entities.User user) => user.GetGoogleServiceEmail();
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
    public async Task Does_not_fire_outside_Application_or_Web_assemblies()
    {
        var source = DomainStub + """

            namespace Some.Domain.Code
            {
                public class Reader
                {
                    public bool Check(Humans.Domain.Entities.UserEmail email) => email.IsOAuth;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new UserEmailLegacyFieldAnalyzer(),
            "Humans.Domain",
            source);

        diagnostics.Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_on_unrelated_property_with_same_name_on_other_type()
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

        diagnostics.Where(d => IsHum0001(d)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Does_not_fire_on_canonical_replacement_Provider_not_null()
    {
        var source = DomainStub + """

            namespace Some.App.Code
            {
                public class Reader
                {
                    public bool IsOAuth(Humans.Domain.Entities.UserEmail email) => email.Provider != null;
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new UserEmailLegacyFieldAnalyzer(),
            "Humans.Application",
            source);

        diagnostics.Where(d => IsHum0001(d)).Should().BeEmpty();
    }
}
