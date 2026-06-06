using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Humans.Analyzers;

/// <summary>
/// HUM0030 -- date/time format-string literals may live only in the single sanctioned
/// formatting home (Humans.Application.Extensions.DateFormattingExtensions). Anywhere else a
/// hand-rolled custom format string is forbidden; call a named formatter on the home instead.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class DateTimeFormatStringAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0030";

    private const string HomeTypeFullName = "Humans.Application.Extensions.DateFormattingExtensions";
    private const string MigrationsNamespacePrefix = "Humans.Infrastructure.Migrations";

    private static readonly LocalizableString Title = "Hand-rolled date/time format string";

    private static readonly LocalizableString MessageFormat =
        "Date/time format string \"{0}\" must not be hand-rolled here. Call a named formatter on " +
        "Humans.Application.Extensions.DateFormattingExtensions (or add one there) -- it is the single " +
        "sanctioned home for date/time format strings.";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Custom (multi-character) date/time format strings on DateTime/DateTimeOffset/DateOnly/TimeOnly " +
            "or NodaTime value types, custom interpolation format clauses, and NodaTime *Pattern.Create(...) " +
            "calls must route through the named extensions in the single formatting home.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    private static readonly ImmutableHashSet<string> ProductionAssemblies =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "Humans.Application", "Humans.Domain", "Humans.Infrastructure", "Humans.Web");

    private static readonly string[] TargetTypeMetadataNames =
    [
        "System.DateTime", "System.DateTimeOffset", "System.DateOnly", "System.TimeOnly",
        "NodaTime.Instant", "NodaTime.LocalDate", "NodaTime.LocalTime", "NodaTime.LocalDateTime",
        "NodaTime.ZonedDateTime", "NodaTime.OffsetDateTime", "NodaTime.OffsetDate", "NodaTime.OffsetTime",
        "NodaTime.Duration", "NodaTime.Period", "NodaTime.YearMonth", "NodaTime.AnnualDate",
    ];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        if (!ProductionAssemblies.Contains(context.Compilation.Assembly.Name))
            return;

        var targets = ImmutableHashSet.CreateBuilder<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var name in TargetTypeMetadataNames)
        {
            var symbol = context.Compilation.GetTypeByMetadataName(name);
            if (symbol is not null)
                targets.Add(symbol);
        }
        var targetTypes = targets.ToImmutable();
        if (targetTypes.IsEmpty)
            return;

        context.RegisterOperationAction(ctx => AnalyzeInvocation(ctx, targetTypes), OperationKind.Invocation);
        context.RegisterOperationAction(ctx => AnalyzeInterpolation(ctx, targetTypes), OperationKind.Interpolation);
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context, ImmutableHashSet<INamedTypeSymbol> targetTypes)
    {
        if (IsExempt(context.ContainingSymbol, context.Operation.Syntax.SyntaxTree.FilePath))
            return;

        var op = (IInvocationOperation)context.Operation;
        var method = op.TargetMethod;

        // <dateTimeValue>.ToString("<custom format>", ...)
        if (string.Equals(method.Name, "ToString", StringComparison.Ordinal) &&
            Unwrap(op.Instance?.Type) is { } receiver && targetTypes.Contains(receiver) &&
            method.Parameters.Length >= 1 &&
            method.Parameters[0].Type.SpecialType == SpecialType.System_String &&
            op.Arguments.Length >= 1 &&
            TryGetCustomFormat(op.Arguments[0].Value, out var fmt))
        {
            Report(context, op.Syntax.GetLocation(), fmt);
            return;
        }

        // NodaTime.Text.*Pattern.Create*("<literal>")
        if (method.IsStatic &&
            method.Name.StartsWith("Create", StringComparison.Ordinal) &&
            method.ContainingType is { } ct &&
            ct.Name.EndsWith("Pattern", StringComparison.Ordinal) &&
            string.Equals(ct.ContainingNamespace?.ToDisplayString(), "NodaTime.Text", StringComparison.Ordinal) &&
            op.Arguments.Length >= 1 &&
            TryGetCustomFormat(op.Arguments[0].Value, out var patternText))
        {
            Report(context, op.Syntax.GetLocation(), patternText);
        }
    }

    private static void AnalyzeInterpolation(OperationAnalysisContext context, ImmutableHashSet<INamedTypeSymbol> targetTypes)
    {
        if (IsExempt(context.ContainingSymbol, context.Operation.Syntax.SyntaxTree.FilePath))
            return;

        var op = (IInterpolationOperation)context.Operation;
        if (op.FormatString is null)
            return;
        if (Unwrap(op.Expression.Type) is not { } exprType || !targetTypes.Contains(exprType))
            return;
        if (TryGetCustomFormat(op.FormatString, out var fmt))
            Report(context, op.Syntax.GetLocation(), fmt);
    }

    private static INamedTypeSymbol? Unwrap(ITypeSymbol? type)
    {
        if (type is not INamedTypeSymbol named)
            return null;
        if (named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T &&
            named.TypeArguments.FirstOrDefault() is INamedTypeSymbol arg)
            return arg;
        return named;
    }

    private static bool TryGetCustomFormat(IOperation operation, out string format)
    {
        format = "";
        var constant = operation.ConstantValue;
        if (!constant.HasValue || constant.Value is not string s)
            return false;
        if (s.Length < 2) // single-char standard specifiers ("d","g","o",...) are allowed in v1
            return false;
        format = s;
        return true;
    }

    private static void Report(OperationAnalysisContext context, Location location, string format) =>
        context.ReportDiagnostic(Diagnostic.Create(Rule, location, format));

    private static bool IsExempt(ISymbol? containingSymbol, string? filePath)
    {
        for (var type = containingSymbol?.ContainingType; type is not null; type = type.ContainingType)
        {
            if (string.Equals(type.ToDisplayString(), HomeTypeFullName, StringComparison.Ordinal))
                return true;
        }

        var ns = containingSymbol?.ContainingNamespace?.ToDisplayString();
        if (ns is not null && ns.StartsWith(MigrationsNamespacePrefix, StringComparison.Ordinal))
            return true;

        if (!string.IsNullOrEmpty(filePath) &&
            filePath!.Replace('\\', '/').Contains("/Humans.Infrastructure/Migrations/", StringComparison.Ordinal))
            return true;

        return false;
    }
}
