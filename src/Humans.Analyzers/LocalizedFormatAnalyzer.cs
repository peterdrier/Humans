using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Humans.Analyzers;

/// <summary>
/// HUM0045 — a localized string with arguments is <c>localizer[key, args]</c>. The indexer already runs
/// <c>string.Format(CultureInfo.CurrentCulture, …)</c>, so a hand-written <c>string.Format</c> over a
/// localized string, or a private helper wrapping one, is a second way to do the same thing.
/// </summary>
/// <remarks>Views are Razor-generated code, so this analyzer opts in to generated code mapped to a .cshtml line.</remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LocalizedFormatAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0045";

    private const string LocalizedStringFullName = "Microsoft.Extensions.Localization.LocalizedString";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "string.Format over a localized string",
        messageFormat: "Pass the arguments to the localizer instead: localizer[key, args] formats with the current culture",
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "IStringLocalizer's indexer formats its arguments with the current culture. Formatting a localized " +
            "string by hand duplicates it.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(
            GeneratedCodeAnalysisFlags.Analyze | GeneratedCodeAnalysisFlags.ReportDiagnostics);
        context.EnableConcurrentExecution();
        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var op = (IInvocationOperation)context.Operation;
        if (!string.Equals(op.TargetMethod.Name, "Format", StringComparison.Ordinal)
            || op.TargetMethod.ContainingType?.SpecialType != SpecialType.System_String)
            return;

        var format = op.Arguments.FirstOrDefault(a =>
            string.Equals(a.Parameter?.Name, "format", StringComparison.Ordinal));
        if (format is null || !IsLocalized(format.Value))
            return;

        var location = op.Syntax.GetLocation();
        if (context.IsGeneratedCode && !location.GetMappedLineSpan().HasMappedPath)
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rule, location));
    }

    private static bool IsLocalized(IOperation value)
    {
        while (value is IConversionOperation conversion)
            value = conversion.Operand;

        if (value is IPropertyReferenceOperation { Instance: { } instance } property
            && string.Equals(property.Property.Name, "Value", StringComparison.Ordinal))
            value = instance;

        return string.Equals(value.Type?.ToDisplayString(), LocalizedStringFullName, StringComparison.Ordinal);
    }
}
