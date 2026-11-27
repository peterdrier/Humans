using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Humans.Analyzers.Tests;

public class RequestScopedCancellationOnExternalWriteAnalyzerTests
{
    // Stubs mirror the production shapes the analyzer matches by name:
    // the MVC verb attributes, HttpContext.RequestAborted, and the two
    // Humans.Base.Attributes markers.
    private const string Stubs = """
        namespace Microsoft.AspNetCore.Http
        {
            public abstract class HttpContext
            {
                public System.Threading.CancellationToken RequestAborted { get; }
            }
        }

        namespace Microsoft.AspNetCore.Mvc
        {
            public abstract class ControllerBase
            {
                public Microsoft.AspNetCore.Http.HttpContext HttpContext { get; } = null!;
            }

            public sealed class HttpGetAttribute : System.Attribute { }
            public sealed class HttpPostAttribute : System.Attribute { }
            public sealed class HttpPutAttribute : System.Attribute { }
            public sealed class HttpDeleteAttribute : System.Attribute { }
            public sealed class HttpPatchAttribute : System.Attribute { }
        }

        namespace Humans.Base.Attributes
        {
            [System.AttributeUsage(System.AttributeTargets.Method)]
            public sealed class ExternalWriteAttribute : System.Attribute { }

            public sealed class GrandfatheredAttribute : System.Attribute
            {
                public GrandfatheredAttribute(string ruleId, string justification, string since, string issueRef) { }
            }
        }

        namespace Humans.Base.Interfaces
        {
            public interface ISyncService
            {
                [Humans.Base.Attributes.ExternalWrite]
                System.Threading.Tasks.Task SyncAsync(System.Threading.CancellationToken ct);

                System.Threading.Tasks.Task PreviewAsync(System.Threading.CancellationToken ct);
            }
        }
        """;

    private static bool IsHum0033(Diagnostic d) =>
        string.Equals(
            d.Id,
            RequestScopedCancellationOnExternalWriteAnalyzer.DiagnosticId,
            StringComparison.Ordinal);

    private static Task<System.Collections.Immutable.ImmutableArray<Diagnostic>> RunAsync(
        string source, string assemblyName = "Humans.Web") =>
        AnalyzerTestHarness.RunAsync(
            new RequestScopedCancellationOnExternalWriteAnalyzer(),
            assemblyName,
            Stubs + source);

    /// <summary>Wraps a controller body in the production namespace shape.</summary>
    private static string Controller(string body) => $$"""

        namespace Humans.Web.Controllers
        {
            public sealed class SyncController : Microsoft.AspNetCore.Mvc.ControllerBase
            {
                private readonly Humans.Base.Interfaces.ISyncService _sync = null!;

        {{body}}
            }
        }
        """;

    [HumansFact]
    public async Task Analyze_PostPassesRequestAborted_Fires()
    {
        var diagnostics = await RunAsync(Controller("""
                    [Microsoft.AspNetCore.Mvc.HttpPost]
                    public System.Threading.Tasks.Task Execute() =>
                        _sync.SyncAsync(HttpContext.RequestAborted);
            """));

        var diagnostic = diagnostics.Should().ContainSingle(d => IsHum0033(d)).Subject;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Error);
        diagnostic.GetMessage().Should().Contain("Execute").And.Contain("SyncAsync");
    }

    [HumansFact]
    public async Task Analyze_PostPassesOwnToken_Fires()
    {
        var diagnostics = await RunAsync(Controller("""
                    [Microsoft.AspNetCore.Mvc.HttpPost]
                    public System.Threading.Tasks.Task Execute(System.Threading.CancellationToken ct) =>
                        _sync.SyncAsync(ct);
            """));

        diagnostics.Should().ContainSingle(d => IsHum0033(d))
            .Which.Severity.Should().Be(DiagnosticSeverity.Error);
    }

    [HumansTheory]
    [InlineData("HttpPut")]
    [InlineData("HttpDelete")]
    [InlineData("HttpPatch")]
    public async Task Analyze_StateChangingVerbs_AllFire(string verb)
    {
        var diagnostics = await RunAsync(Controller($$"""
                    [Microsoft.AspNetCore.Mvc.{{verb}}]
                    public System.Threading.Tasks.Task Execute() =>
                        _sync.SyncAsync(HttpContext.RequestAborted);
            """));

        diagnostics.Should().ContainSingle(d => IsHum0033(d));
    }

    [HumansFact]
    public async Task Analyze_GetAction_IsSilent()
    {
        var diagnostics = await RunAsync(Controller("""
                    [Microsoft.AspNetCore.Mvc.HttpGet]
                    public System.Threading.Tasks.Task Preview() =>
                        _sync.SyncAsync(HttpContext.RequestAborted);
            """));

        diagnostics.Should().NotContain(d => IsHum0033(d));
    }

    [HumansFact]
    public async Task Analyze_TokenNone_IsSilent()
    {
        var diagnostics = await RunAsync(Controller("""
                    [Microsoft.AspNetCore.Mvc.HttpPost]
                    public System.Threading.Tasks.Task Execute() =>
                        _sync.SyncAsync(System.Threading.CancellationToken.None);
            """));

        diagnostics.Should().NotContain(d => IsHum0033(d));
    }

    [HumansFact]
    public async Task Analyze_UnmarkedTarget_IsSilent()
    {
        var diagnostics = await RunAsync(Controller("""
                    [Microsoft.AspNetCore.Mvc.HttpPost]
                    public System.Threading.Tasks.Task Execute() =>
                        _sync.PreviewAsync(HttpContext.RequestAborted);
            """));

        diagnostics.Should().NotContain(d => IsHum0033(d));
    }

    [HumansFact]
    public async Task Analyze_ViaImplementingClass_Fires()
    {
        const string implementation = """

            namespace Humans.Application.Services
            {
                public sealed class SyncService : Humans.Base.Interfaces.ISyncService
                {
                    public System.Threading.Tasks.Task SyncAsync(System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.Task.CompletedTask;

                    public System.Threading.Tasks.Task PreviewAsync(System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """;

        var diagnostics = await RunAsync(implementation + """

            namespace Humans.Web.Controllers
            {
                public sealed class SyncController : Microsoft.AspNetCore.Mvc.ControllerBase
                {
                    private readonly Humans.Application.Services.SyncService _sync = null!;

                    [Microsoft.AspNetCore.Mvc.HttpPost]
                    public System.Threading.Tasks.Task Execute() =>
                        _sync.SyncAsync(HttpContext.RequestAborted);
                }
            }
            """);

        diagnostics.Should().ContainSingle(d => IsHum0033(d));
    }

    [HumansFact]
    public async Task Analyze_GrandfatheredAction_Warns()
    {
        var diagnostics = await RunAsync(Controller("""
                    [Microsoft.AspNetCore.Mvc.HttpPost]
                    [Humans.Base.Attributes.Grandfathered("HUM0033", "legacy", "2026-08-05", "nobodies-collective/Humans#950")]
                    public System.Threading.Tasks.Task Execute() =>
                        _sync.SyncAsync(HttpContext.RequestAborted);
            """));

        diagnostics.Should().ContainSingle(d => IsHum0033(d))
            .Which.Severity.Should().Be(DiagnosticSeverity.Warning);
    }

    [HumansFact]
    public async Task Analyze_SectionAssembly_Fires()
    {
        // The Section : ISection entry point is what makes AssemblyScope.IsSection
        // recognise the "Humans.Store" compilation below as a section.
        const string source = """
            namespace Humans.Base.Interfaces
            {
                public interface ISection { }
            }

            namespace Humans.Store
            {
                public sealed class Section : Humans.Base.Interfaces.ISection { }
            }

            namespace Humans.Store.Controllers
            {
                public sealed class StoreController : Microsoft.AspNetCore.Mvc.ControllerBase
                {
                    private readonly Humans.Base.Interfaces.ISyncService _sync = null!;

                    [Microsoft.AspNetCore.Mvc.HttpPost]
                    public System.Threading.Tasks.Task Execute() =>
                        _sync.SyncAsync(HttpContext.RequestAborted);
                }
            }
            """;

        var diagnostics = await AnalyzerTestHarness.RunAsync(
            new RequestScopedCancellationOnExternalWriteAnalyzer(),
            "Humans.Store",
            source + Stubs);

        diagnostics.Should().ContainSingle(d => IsHum0033(d));
    }
}
