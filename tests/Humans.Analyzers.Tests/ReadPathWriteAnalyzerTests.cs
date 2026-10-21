using AwesomeAssertions;
using Microsoft.CodeAnalysis;
using Xunit;

namespace Humans.Analyzers.Tests;

public class ReadPathWriteAnalyzerTests
{
    // Stubs mirror the production shapes the analyzer matches by name: the MVC
    // verb attributes and base types, [ExternalWrite], and an I*Write interface.
    private const string Stubs = """
        namespace Microsoft.AspNetCore.Mvc
        {
            public abstract class ControllerBase { }
            public abstract class ViewComponent { }

            public sealed class HttpGetAttribute : System.Attribute { }
            public sealed class HttpHeadAttribute : System.Attribute { }
            public sealed class HttpPostAttribute : System.Attribute { }
            public sealed class NonActionAttribute : System.Attribute { }

            public sealed class AcceptVerbsAttribute : System.Attribute
            {
                public AcceptVerbsAttribute(string method) { }
                public AcceptVerbsAttribute(params string[] methods) { }
            }
        }

        namespace Humans.Base.Attributes
        {
            [System.AttributeUsage(System.AttributeTargets.Method)]
            public sealed class ExternalWriteAttribute : System.Attribute { }
        }

        namespace Humans.Things.Contracts
        {
            public interface IThingService
            {
                System.Threading.Tasks.Task GetAsync();

                [Humans.Base.Attributes.ExternalWrite]
                System.Threading.Tasks.Task PushAsync();
            }

            public interface IThingWrite
            {
                System.Threading.Tasks.Task SaveAsync();
            }

            public interface IThingManagement : IThingService, IThingWrite { }
        }
        """;

    private static bool IsHum0038(Diagnostic d) =>
        string.Equals(d.Id, ReadPathWriteAnalyzer.DiagnosticId, StringComparison.Ordinal);

    private static Task<System.Collections.Immutable.ImmutableArray<Diagnostic>> RunAsync(string source) =>
        AnalyzerTestHarness.RunAsync(new ReadPathWriteAnalyzer(), "Humans.Things", Stubs + source);

    private static string Controller(string body) => $$"""

        namespace Humans.Things.Controllers
        {
            public sealed class ThingController : Microsoft.AspNetCore.Mvc.ControllerBase
            {
                private readonly Humans.Things.Contracts.IThingManagement _things = null!;

        {{body}}
            }
        }
        """;

    [HumansTheory]
    [InlineData("[Microsoft.AspNetCore.Mvc.HttpGet]")]
    [InlineData("[Microsoft.AspNetCore.Mvc.HttpHead]")]
    [InlineData("[Microsoft.AspNetCore.Mvc.HttpGet, Microsoft.AspNetCore.Mvc.HttpPost]")]
    [InlineData("[Microsoft.AspNetCore.Mvc.AcceptVerbs(\"GET\")]")]
    [InlineData("[Microsoft.AspNetCore.Mvc.AcceptVerbs(\"get\")]")]
    [InlineData("[Microsoft.AspNetCore.Mvc.AcceptVerbs(\"GET\", \"POST\")]")]
    [InlineData("[Microsoft.AspNetCore.Mvc.AcceptVerbs(\"POST\", \"head\")]")]
    [InlineData("")]
    public async Task Fires_when_an_action_answering_GET_calls_an_IWrite_member(string verbs)
    {
        var diagnostics = await RunAsync(Controller($$"""
                    {{verbs}}
                    public System.Threading.Tasks.Task Index() => _things.SaveAsync();
            """));

        var diagnostic = diagnostics.Should().ContainSingle(d => IsHum0038(d)).Subject;
        diagnostic.Severity.Should().Be(DiagnosticSeverity.Error);
        diagnostic.GetMessage().Should().Contain("ThingController.Index").And.Contain("IThingWrite.SaveAsync");
    }

    [HumansFact]
    public async Task Fires_when_a_GET_action_calls_an_ExternalWrite_method()
    {
        var diagnostics = await RunAsync(Controller("""
                    [Microsoft.AspNetCore.Mvc.HttpGet]
                    public System.Threading.Tasks.Task Index() => _things.PushAsync();
            """));

        diagnostics.Should().ContainSingle(d => IsHum0038(d));
    }

    [HumansFact]
    public async Task Fires_inside_a_lambda_in_a_GET_action()
    {
        var diagnostics = await RunAsync(Controller("""
                    [Microsoft.AspNetCore.Mvc.HttpGet]
                    public System.Threading.Tasks.Task Index() =>
                        System.Threading.Tasks.Task.Run(() => _things.SaveAsync());
            """));

        diagnostics.Should().ContainSingle(d => IsHum0038(d));
    }

    [HumansFact]
    public async Task Fires_in_a_view_component_invoke()
    {
        var diagnostics = await RunAsync("""

            namespace Humans.Things.ViewComponents
            {
                public sealed class ThingViewComponent : Microsoft.AspNetCore.Mvc.ViewComponent
                {
                    private readonly Humans.Things.Contracts.IThingWrite _write = null!;

                    public System.Threading.Tasks.Task InvokeAsync() => _write.SaveAsync();
                }
            }
            """);

        diagnostics.Should().ContainSingle(d => IsHum0038(d));
    }

    [HumansFact]
    public async Task Fires_when_called_through_the_implementing_class()
    {
        var diagnostics = await RunAsync("""

            namespace Humans.Things.Services
            {
                public sealed class ThingWriter : Humans.Things.Contracts.IThingWrite
                {
                    public System.Threading.Tasks.Task SaveAsync() => System.Threading.Tasks.Task.CompletedTask;
                }
            }

            namespace Humans.Things.Controllers
            {
                public sealed class ThingController : Microsoft.AspNetCore.Mvc.ControllerBase
                {
                    private readonly Humans.Things.Services.ThingWriter _writer = null!;

                    [Microsoft.AspNetCore.Mvc.HttpGet]
                    public System.Threading.Tasks.Task Index() => _writer.SaveAsync();
                }
            }
            """);

        diagnostics.Should().ContainSingle(d => IsHum0038(d));
    }

    [HumansTheory]
    [InlineData("[Microsoft.AspNetCore.Mvc.HttpPost]")]
    [InlineData("[Microsoft.AspNetCore.Mvc.AcceptVerbs(\"POST\")]")]
    [InlineData("[Microsoft.AspNetCore.Mvc.AcceptVerbs(\"POST\", \"PUT\")]")]
    public async Task Does_not_fire_for_a_POST_action(string verbs)
    {
        var diagnostics = await RunAsync(Controller($$"""
                    {{verbs}}
                    public System.Threading.Tasks.Task Save() => _things.SaveAsync();
            """));

        diagnostics.Should().NotContain(d => IsHum0038(d));
    }

    [HumansFact]
    public async Task Does_not_fire_for_a_read_in_a_GET_action()
    {
        var diagnostics = await RunAsync(Controller("""
                    [Microsoft.AspNetCore.Mvc.HttpGet]
                    public System.Threading.Tasks.Task Index() => _things.GetAsync();
            """));

        diagnostics.Should().NotContain(d => IsHum0038(d));
    }

    [HumansFact]
    public async Task Does_not_fire_for_a_private_helper_or_a_NonAction()
    {
        var diagnostics = await RunAsync(Controller("""
                    private System.Threading.Tasks.Task Helper() => _things.SaveAsync();

                    [Microsoft.AspNetCore.Mvc.NonAction]
                    public System.Threading.Tasks.Task NotAnAction() => _things.SaveAsync();
            """));

        diagnostics.Should().NotContain(d => IsHum0038(d));
    }
}
