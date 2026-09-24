using AwesomeAssertions;

namespace Humans.Analyzers.Tests;

public class StringViewComponentInvocationAnalyzerTests
{
    private const string Stubs = """
        namespace Microsoft.AspNetCore.Mvc
        {
            using System;
            using System.Threading.Tasks;

            public interface IViewComponentHelper
            {
                Task<object> InvokeAsync(string name, object? arguments);
                Task<object> InvokeAsync(Type componentType, object? arguments);
            }

            public abstract class Controller
            {
                public virtual object ViewComponent(string componentName) => componentName;
                public virtual object ViewComponent(Type componentType) => componentType;
            }

            public class ViewComponentResult
            {
                public string? ViewComponentName { get; set; }
                public Type? ViewComponentType { get; set; }
                public object? Arguments { get; set; }
            }
        }

        namespace Microsoft.AspNetCore.Mvc.Rendering
        {
            using System;
            using System.Threading.Tasks;
            using Microsoft.AspNetCore.Mvc;

            public static class ViewComponentHelperExtensions
            {
                public static Task<object> InvokeAsync(this IViewComponentHelper helper, string name) => null!;
                public static Task<object> InvokeAsync(this IViewComponentHelper helper, Type componentType) => null!;
                public static Task<object> InvokeAsync<TComponent>(this IViewComponentHelper helper) => null!;
            }
        }
        """;

    private static bool IsHum0036(Microsoft.CodeAnalysis.Diagnostic d) =>
        string.Equals(d.Id, StringViewComponentInvocationAnalyzer.DiagnosticId, StringComparison.Ordinal);

    private static Task<System.Collections.Immutable.ImmutableArray<Microsoft.CodeAnalysis.Diagnostic>> RunAsync(string source) =>
        AnalyzerTestHarness.RunAsync(new StringViewComponentInvocationAnalyzer(), "Humans.Shifts", source);

    [HumansFact]
    public async Task Fires_on_every_string_name_overload()
    {
        var source = Stubs + """

            namespace Humans.Shifts
            {
                using Microsoft.AspNetCore.Mvc;
                using Microsoft.AspNetCore.Mvc.Rendering;

                public sealed class Caller : Controller
                {
                    public void Run(IViewComponentHelper component)
                    {
                        _ = component.InvokeAsync("ShiftCards", new { Id = 1 });
                        _ = component.InvokeAsync("ShiftCards");
                        _ = ViewComponent("ShiftCards");
                    }
                }
            }
            """;

        var diagnostics = (await RunAsync(source)).Where(IsHum0036).ToList();

        diagnostics.Should().HaveCount(3);
        diagnostics.Should().OnlyContain(d => d.GetMessage(null).Contains("\"ShiftCards\"", StringComparison.Ordinal));
    }

    [HumansFact]
    public async Task Fires_on_ViewComponentResult_name_assignment()
    {
        var source = Stubs + """

            namespace Humans.Shifts
            {
                using Microsoft.AspNetCore.Mvc;

                public sealed class Caller
                {
                    public ViewComponentResult Run(ViewComponentResult later)
                    {
                        later.ViewComponentName = "ShiftCards";
                        later.ViewComponentName = null;
                        return new ViewComponentResult { ViewComponentName = "ShiftCards", Arguments = new { Id = 1 } };
                    }
                }
            }
            """;

        var diagnostics = (await RunAsync(source)).Where(IsHum0036).ToList();

        diagnostics.Should().HaveCount(2);
        diagnostics.Should().OnlyContain(d => d.GetMessage(null).Contains("\"ShiftCards\"", StringComparison.Ordinal));
    }

    [HumansFact]
    public async Task Does_not_fire_on_Type_invocations()
    {
        var source = Stubs + """

            namespace Humans.Shifts
            {
                using Microsoft.AspNetCore.Mvc;
                using Microsoft.AspNetCore.Mvc.Rendering;

                public sealed class ShiftCards { }

                public sealed class Caller : Controller
                {
                    public void Run(IViewComponentHelper component)
                    {
                        _ = component.InvokeAsync(typeof(ShiftCards), new { Id = 1 });
                        _ = component.InvokeAsync(typeof(ShiftCards));
                        _ = component.InvokeAsync<ShiftCards>();
                        _ = ViewComponent(typeof(ShiftCards));
                        _ = new ViewComponentResult { ViewComponentType = typeof(ShiftCards) };
                    }
                }
            }
            """;

        (await RunAsync(source)).Where(IsHum0036).Should().BeEmpty();
    }

    [HumansFact]
    public async Task Generated_code_reports_only_line_mapped_sites()
    {
        // Razor output: a <vc:> tag helper's own InvokeAsync("Name") sits at an unmapped
        // location and is skipped; a hand-written view call maps through #line to the .cshtml.
        var source = "// <auto-generated/>\n" + Stubs + """

            namespace AspNetCoreGeneratedDocument
            {
                using Microsoft.AspNetCore.Mvc;
                using Microsoft.AspNetCore.Mvc.Rendering;

                public sealed class Views_Shifts_Index
                {
                    public void TagHelperInternals(IViewComponentHelper helper)
                    {
                        _ = helper.InvokeAsync("TagHelperTarget", null);
                    }

                    public void Render(IViewComponentHelper Component)
                    {
            #line 100 "/src/Sections/Humans.Shifts/Views/Shifts/Index.cshtml"
                        _ = Component.InvokeAsync("HandWritten");
            #line default
                    }
                }
            }
            """;

        var diagnostics = (await RunAsync(source)).Where(IsHum0036).ToList();

        var single = diagnostics.Should().ContainSingle().Subject;
        single.GetMessage(null).Should().Contain("\"HandWritten\"");
        var span = single.Location.GetMappedLineSpan();
        span.Path.Should().EndWith("Index.cshtml");
        span.StartLinePosition.Line.Should().Be(99);
    }
}
