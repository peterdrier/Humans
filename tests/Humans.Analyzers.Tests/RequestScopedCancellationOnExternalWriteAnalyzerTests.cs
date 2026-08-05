using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Humans.Analyzers.Tests;

public class RequestScopedCancellationOnExternalWriteAnalyzerTests
{
    // Stubs mirror the production shapes the analyzer matches by name:
    // the MVC verb attributes, HttpContext.RequestAborted, and the two
    // Humans.Application.Architecture markers.
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

        namespace Humans.Application.Architecture
        {
            [System.AttributeUsage(System.AttributeTargets.Method)]
            public sealed class ExternalWriteAttribute : System.Attribute { }

            public sealed class GrandfatheredAttribute : System.Attribute
            {
                public GrandfatheredAttribute(string ruleId, string justification, string since, string issueRef) { }
            }
        }

        namespace Humans.Application.Interfaces
        {
            public interface ISyncService
            {
                [Humans.Application.Architecture.ExternalWrite]
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
                private readonly Humans.Application.Interfaces.ISyncService _sync = null!;

        {{body}}
            }
        }
        """;

    [HumansFact]
    public async Task Fires_when_post_action_passes_RequestAborted_to_external_write()
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
    public async Task Fires_when_post_action_passes_its_own_cancellation_token_parameter()
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
    public async Task Fires_for_every_state_changing_verb(string verb)
    {
        var diagnostics = await RunAsync(Controller($$"""
                    [Microsoft.AspNetCore.Mvc.{{verb}}]
                    public System.Threading.Tasks.Task Execute() =>
                        _sync.SyncAsync(HttpContext.RequestAborted);
            """));

        diagnostics.Should().ContainSingle(d => IsHum0033(d));
    }

    [HumansFact]
    public async Task Does_not_fire_for_get_action_because_reads_may_be_abandoned()
    {
        var diagnostics = await RunAsync(Controller("""
                    [Microsoft.AspNetCore.Mvc.HttpGet]
                    public System.Threading.Tasks.Task Preview() =>
                        _sync.SyncAsync(HttpContext.RequestAborted);
            """));

        diagnostics.Should().NotContain(d => IsHum0033(d));
    }

    [HumansFact]
    public async Task Does_not_fire_when_token_is_None()
    {
        var diagnostics = await RunAsync(Controller("""
                    [Microsoft.AspNetCore.Mvc.HttpPost]
                    public System.Threading.Tasks.Task Execute() =>
                        _sync.SyncAsync(System.Threading.CancellationToken.None);
            """));

        diagnostics.Should().NotContain(d => IsHum0033(d));
    }

    [HumansFact]
    public async Task Does_not_fire_when_target_method_is_not_marked()
    {
        var diagnostics = await RunAsync(Controller("""
                    [Microsoft.AspNetCore.Mvc.HttpPost]
                    public System.Threading.Tasks.Task Execute() =>
                        _sync.PreviewAsync(HttpContext.RequestAborted);
            """));

        diagnostics.Should().NotContain(d => IsHum0033(d));
    }

    [HumansFact]
    public async Task Fires_when_called_through_the_implementing_class()
    {
        const string Implementation = """

            namespace Humans.Application.Services
            {
                public sealed class SyncService : Humans.Application.Interfaces.ISyncService
                {
                    public System.Threading.Tasks.Task SyncAsync(System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.Task.CompletedTask;

                    public System.Threading.Tasks.Task PreviewAsync(System.Threading.CancellationToken ct) =>
                        System.Threading.Tasks.Task.CompletedTask;
                }
            }
            """;

        var diagnostics = await RunAsync(Implementation + """

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
    public async Task Downgrades_to_warning_when_action_is_grandfathered()
    {
        var diagnostics = await RunAsync(Controller("""
                    [Microsoft.AspNetCore.Mvc.HttpPost]
                    [Humans.Application.Architecture.Grandfathered("HUM0033", "legacy", "2026-08-05", "nobodies-collective/Humans#950")]
                    public System.Threading.Tasks.Task Execute() =>
                        _sync.SyncAsync(HttpContext.RequestAborted);
            """));

        diagnostics.Should().ContainSingle(d => IsHum0033(d))
            .Which.Severity.Should().Be(DiagnosticSeverity.Warning);
    }

    [HumansFact]
    public async Task Does_not_fire_outside_the_web_assembly()
    {
        var diagnostics = await RunAsync(
            Controller("""
                    [Microsoft.AspNetCore.Mvc.HttpPost]
                    public System.Threading.Tasks.Task Execute() =>
                        _sync.SyncAsync(HttpContext.RequestAborted);
            """),
            assemblyName: "Humans.Application");

        diagnostics.Should().NotContain(d => IsHum0033(d));
    }
}
