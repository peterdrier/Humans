using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Humans.Analyzers;

/// <summary>
/// HUM0010 / HUM0011 — Hard removal deadlines via
/// <c>[ExpiresOn("yyyy-MM-dd", graceDays: N, reason: "...")]</c>.
///
/// <para>
/// <b>HUM0010 (usage):</b> every reference to a decorated symbol fires a
/// warning before the date and an error on/after it. Callers must migrate
/// before the deadline.
/// </para>
///
/// <para>
/// <b>HUM0011 (declaration):</b> the decorated symbol itself is clean before
/// the date, a warning during the <c>graceDays</c> window (default 7), and
/// an error after. The grace period gives the author time to delete the
/// symbol after callers have migrated.
/// </para>
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ExpiresOnAnalyzer : DiagnosticAnalyzer
{
    public const string UsageDiagnosticId = "HUM0010";
    public const string DeclarationDiagnosticId = "HUM0011";

    private const string ExpiresOnAttributeFullName =
        "Humans.Domain.Architecture.ExpiresOnAttribute";

    /// <summary>
    /// Test hook. When non-null, replaces <c>DateTime.UtcNow.Date</c> for
    /// computing diagnostic severity. Production code never sets this.
    /// </summary>
    /// <remarks>
    /// Backed by <see cref="AsyncLocal{T}"/> so concurrent test collections
    /// cannot bleed overrides into each other. AsyncLocal (rather than
    /// ThreadStatic) is required because Roslyn schedules analyzer callbacks
    /// on its own threadpool via <c>EnableConcurrentExecution</c> — the
    /// override must flow through async/await and Task scheduling to reach
    /// <c>OnCompilationStart</c>. Production code always sees <c>null</c>.
    /// </remarks>
    private static readonly AsyncLocal<Func<DateTime>?> _todayOverride = new();

    public static Func<DateTime>? TodayOverride
    {
        get => _todayOverride.Value;
        set => _todayOverride.Value = value;
    }

    private static readonly LocalizableString UsageTitle =
        "Reference to symbol with expiration deadline";

    // The format is a single positional slot — the analyzer assembles the
    // full message dynamically because severity-dependent wording would
    // otherwise require multiple descriptors. RS1032 requires the format
    // itself to be a clean single sentence with no trailing period.
    private static readonly LocalizableString UsageMessageFormat = "{0}";

    public static readonly DiagnosticDescriptor UsageRule = new(
        id: UsageDiagnosticId,
        title: UsageTitle,
        messageFormat: UsageMessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "[ExpiresOn(date)] sets a hard deadline. References emit a warning " +
            "before the date and an error on/after the date. Migrate callers " +
            "before the deadline.");

    private static readonly LocalizableString DeclarationTitle =
        "Declaration past its expiration deadline";

    private static readonly LocalizableString DeclarationMessageFormat = "{0}";

    public static readonly DiagnosticDescriptor DeclarationRule = new(
        id: DeclarationDiagnosticId,
        title: DeclarationTitle,
        messageFormat: DeclarationMessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "After the [ExpiresOn] date, the decorated symbol itself becomes a " +
            "warning for graceDays (default 7), then an error. Delete the symbol " +
            "during the grace period.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(UsageRule, DeclarationRule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        var attributeType = context.Compilation.GetTypeByMetadataName(ExpiresOnAttributeFullName);
        if (attributeType is null)
            return;

        var today = (TodayOverride?.Invoke() ?? DateTime.UtcNow).Date;

        context.RegisterSymbolAction(
            ctx => AnalyzeDeclaration(ctx, attributeType, today),
            SymbolKind.NamedType,
            SymbolKind.Method,
            SymbolKind.Property,
            SymbolKind.Field,
            SymbolKind.Event);

        context.RegisterOperationAction(
            ctx => AnalyzeOperation(ctx, attributeType, today),
            OperationKind.Invocation,
            OperationKind.PropertyReference,
            OperationKind.FieldReference,
            OperationKind.MethodReference,
            OperationKind.EventReference,
            OperationKind.ObjectCreation);
    }

    private static void AnalyzeDeclaration(
        SymbolAnalysisContext context,
        INamedTypeSymbol attributeType,
        DateTime today)
    {
        var attr = FindAttribute(context.Symbol, attributeType);
        if (attr is null)
            return;

        if (!TryReadAttribute(attr, out var date, out var graceDays, out var reason))
            return;

        var graceEnd = date.AddDays(graceDays);
        if (today < date)
            return;

        var location = context.Symbol.Locations.Length > 0
            ? context.Symbol.Locations[0]
            : Location.None;

        DiagnosticSeverity severity;
        string message;
        if (today < graceEnd)
        {
            severity = DiagnosticSeverity.Warning;
            var daysLeft = (graceEnd - today).Days;
            message =
                $"'{context.Symbol.Name}' expired on {Format(date)} and is in its " +
                $"{graceDays}-day grace period. Delete it within {daysLeft} day(s).{FormatReason(reason)}";
        }
        else
        {
            severity = DiagnosticSeverity.Error;
            var daysOver = (today - graceEnd).Days;
            message =
                $"'{context.Symbol.Name}' is past its expiration of {Format(date)} and " +
                $"{graceDays}-day grace period (by {daysOver} day(s)). Delete it now.{FormatReason(reason)}";
        }

        context.ReportDiagnostic(Diagnostic.Create(
            descriptor: DeclarationRule,
            location: location,
            effectiveSeverity: severity,
            additionalLocations: null,
            properties: null,
            messageArgs: new object[] { message }));
    }

    private static void AnalyzeOperation(
        OperationAnalysisContext context,
        INamedTypeSymbol attributeType,
        DateTime today)
    {
        var target = ResolveTarget(context.Operation);
        if (target is null)
            return;

        // Walk the symbol and its containing types so a usage of a member
        // inside an [ExpiresOn] class also fires.
        for (var current = target; current is not null; current = current.ContainingType)
        {
            var attr = FindAttribute(current, attributeType);
            if (attr is null)
                continue;

            // Malformed attribute at this level is a no-op — keep walking so
            // a valid attribute on a containing type still fires.
            if (!TryReadAttribute(attr, out var date, out var _, out var reason))
                continue;

            DiagnosticSeverity severity;
            string message;
            if (today < date)
            {
                severity = DiagnosticSeverity.Warning;
                var daysLeft = (date - today).Days;
                message =
                    $"'{current.Name}' is scheduled to expire on {Format(date)} " +
                    $"(in {daysLeft} day(s)). Migrate before then.{FormatReason(reason)}";
            }
            else
            {
                severity = DiagnosticSeverity.Error;
                var daysOver = (today - date).Days;
                message =
                    $"'{current.Name}' expired on {Format(date)} ({daysOver} day(s) ago). " +
                    $"Remove this reference.{FormatReason(reason)}";
            }

            context.ReportDiagnostic(Diagnostic.Create(
                descriptor: UsageRule,
                location: context.Operation.Syntax.GetLocation(),
                effectiveSeverity: severity,
                additionalLocations: null,
                properties: null,
                messageArgs: new object[] { message }));

            // Report once per usage even if both the member and the
            // containing type are decorated.
            return;
        }
    }

    private static ISymbol? ResolveTarget(IOperation operation) => operation switch
    {
        IInvocationOperation inv => inv.TargetMethod,
        IPropertyReferenceOperation prop => prop.Property,
        IFieldReferenceOperation field => field.Field,
        IMethodReferenceOperation methodRef => methodRef.Method,
        IEventReferenceOperation evt => evt.Event,
        IObjectCreationOperation ctor => ctor.Constructor,
        _ => null,
    };

    private static AttributeData? FindAttribute(ISymbol symbol, INamedTypeSymbol attributeType)
    {
        foreach (var attr in symbol.GetAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(attr.AttributeClass, attributeType))
                return attr;
        }
        return null;
    }

    private static bool TryReadAttribute(
        AttributeData attr,
        out DateTime date,
        out int graceDays,
        out string? reason)
    {
        date = default;
        graceDays = 7;
        reason = null;

        if (attr.ConstructorArguments.Length == 0)
            return false;

        if (attr.ConstructorArguments[0].Value is not string dateText)
            return false;

        if (!DateTime.TryParseExact(
                dateText,
                "yyyy-MM-dd",
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out date))
        {
            return false;
        }

        if (attr.ConstructorArguments.Length >= 2 &&
            attr.ConstructorArguments[1].Value is int g)
        {
            graceDays = g;
        }

        if (attr.ConstructorArguments.Length >= 3 &&
            attr.ConstructorArguments[2].Value is string r)
        {
            reason = r;
        }

        // Named arguments override positional (uncommon, but supported).
        foreach (var named in attr.NamedArguments)
        {
            switch (named.Key)
            {
                case "GraceDays" when named.Value.Value is int gn:
                    graceDays = gn;
                    break;
                case "Reason" when named.Value.Value is string rn:
                    reason = rn;
                    break;
            }
        }

        return true;
    }

    private static string Format(DateTime date) =>
        date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string FormatReason(string? reason) =>
        string.IsNullOrWhiteSpace(reason) ? string.Empty : $" Reason: {reason}";
}
