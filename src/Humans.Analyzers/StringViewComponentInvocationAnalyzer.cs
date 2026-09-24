using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Humans.Analyzers;

/// <summary>
/// HUM0036 — a view component is invoked by Type, never by its string name
/// (design-rules §8b). A name is an unchecked string that survives a rename or a
/// section move and fails only at render time; a Type is checked by the compiler.
/// </summary>
/// <remarks>
/// Views are Razor-generated code, so this analyzer opts in to generated code. Every
/// <c>&lt;vc:…&gt;</c> tag helper compiles to a generated helper that itself calls
/// <c>InvokeAsync("Name", …)</c> at an unmapped location; those are skipped. A
/// hand-written <c>@await Component.InvokeAsync("…")</c> maps through <c>#line</c> to
/// its .cshtml line and is reported there.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class StringViewComponentInvocationAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0036";

    private const string HelperFullName = "Microsoft.AspNetCore.Mvc.IViewComponentHelper";
    private const string HelperExtensionsFullName = "Microsoft.AspNetCore.Mvc.Rendering.ViewComponentHelperExtensions";
    private const string ControllerFullName = "Microsoft.AspNetCore.Mvc.Controller";
    private const string ResultFullName = "Microsoft.AspNetCore.Mvc.ViewComponentResult";

    private static readonly LocalizableString Title =
        "String-name view component invocation";

    private static readonly LocalizableString MessageFormat =
        "View component invoked by name \"{0}\". Invoke it by Type: <vc:…> or InvokeAsync<T> in its own section, "
        + "or a Type from a [ViewComponentSlot] seam across sections (design-rules §8b).";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "IViewComponentHelper.InvokeAsync(string, …), its InvokeAsync(helper, string) extension, " +
            "Controller.ViewComponent(string, …) and ViewComponentResult.ViewComponentName resolve a view " +
            "component by name at render time. " +
            "Invoke by Type so the compiler checks the target (design-rules §8b).");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        // Razor output is generated code; the repo default (None) would make this rule blind to views.
        context.ConfigureGeneratedCodeAnalysis(
            GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
        context.RegisterOperationAction(AnalyzeAssignment, OperationKind.SimpleAssignment);
    }

    // new ViewComponentResult { ViewComponentName = "…" } (or a later assignment) is the same
    // by-name lookup without a method call; ViewComponentType is the Type-checked alternative.
    private static void AnalyzeAssignment(OperationAnalysisContext context)
    {
        var op = (ISimpleAssignmentOperation)context.Operation;
        if (op.Target is not IPropertyReferenceOperation { Property: var property }
            || !string.Equals(property.Name, "ViewComponentName", StringComparison.Ordinal)
            || !string.Equals(property.ContainingType?.ToDisplayString(), ResultFullName, StringComparison.Ordinal))
            return;

        // Assigning null clears the name; only a non-null value selects a component by name.
        if (op.Value.ConstantValue is { HasValue: true, Value: null })
            return;

        var name = op.Value.ConstantValue is { HasValue: true, Value: string s }
            ? s
            : op.Value.Syntax.ToString();

        context.ReportDiagnostic(Diagnostic.Create(Rule, op.Syntax.GetLocation(), name));
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var op = (IInvocationOperation)context.Operation;
        var method = op.TargetMethod;
        if (!IsStringNameOverload(method))
            return;

        var location = op.Syntax.GetLocation();

        // Generated and unmapped = the <vc:…> tag helper's own internals, not a hand-written call.
        if (context.IsGeneratedCode && !location.GetMappedLineSpan().HasMappedPath)
            return;

        var nameOrdinal = NameOrdinal(method);
        var nameArgument = op.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == nameOrdinal);
        var name = nameArgument?.Value.ConstantValue is { HasValue: true, Value: string s }
            ? s
            : nameArgument?.Value.Syntax.ToString() ?? "?";

        context.ReportDiagnostic(Diagnostic.Create(Rule, location, name));
    }

    private static bool IsStringNameOverload(IMethodSymbol method)
    {
        var declaring = method.OriginalDefinition;
        while (declaring.OverriddenMethod is { } overridden)
            declaring = overridden.OriginalDefinition;

        var containing = declaring.ContainingType?.ToDisplayString();
        var expectedName = containing switch
        {
            HelperFullName or HelperExtensionsFullName => "InvokeAsync",
            ControllerFullName => "ViewComponent",
            _ => null,
        };
        if (expectedName is null || !string.Equals(declaring.Name, expectedName, StringComparison.Ordinal))
            return false;

        var ordinal = NameOrdinal(declaring);
        return declaring.Parameters.Length > ordinal
            && declaring.Parameters[ordinal].Type.SpecialType == SpecialType.System_String;
    }

    // The extension overload's first parameter is the helper itself; the name follows it.
    private static int NameOrdinal(IMethodSymbol method) =>
        method.IsExtensionMethod ? 1 : 0;
}
