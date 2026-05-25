using System;
using System.Collections.Immutable;
using Humans.Analyzers.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Humans.Analyzers;

/// <summary>
/// HUM0023 -- only EventRepository may write the Events section DbSets.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class EventDbSetWriteAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0023";

    private const string HumansDbContextFullName = "Humans.Infrastructure.Data.HumansDbContext";
    private const string EventRepositoryFullName =
        "Humans.Infrastructure.Repositories.Events.EventRepository";

    private static readonly ImmutableHashSet<string> EventDbSets =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "Events",
            "EventCategories",
            "EventVenues",
            "EventFavourites",
            "EventPreferences",
            "EventGuideSettings",
            "EventModerationActions");

    private static readonly ImmutableHashSet<string> WriteMethods =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "Add",
            "AddRange",
            "Attach",
            "AttachRange",
            "Remove",
            "RemoveRange",
            "Update",
            "UpdateRange");

    private static readonly LocalizableString Title =
        "Event DbSets may only be written by EventRepository";

    private static readonly LocalizableString MessageFormat =
        "'{0}' writes HumansDbContext.{1}. Route Event Guide writes through IEventRepository or the Events application service boundary.";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "The Events section owns the event_* tables. Only EventRepository may call EF write methods " +
            "on Event Guide DbSets exposed by HumansDbContext.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        if (!string.Equals(context.Compilation.Assembly.Name, AssemblyScope.Infrastructure, StringComparison.Ordinal))
            return;

        var dbContextType = context.Compilation.GetTypeByMetadataName(HumansDbContextFullName);
        if (dbContextType is null)
            return;

        var grandfatheredAttr = GrandfatheredCheck.Resolve(context.Compilation);

        context.RegisterOperationAction(
            ctx => AnalyzeInvocation(ctx, dbContextType, grandfatheredAttr),
            OperationKind.Invocation);
    }

    private static void AnalyzeInvocation(
        OperationAnalysisContext context,
        INamedTypeSymbol dbContextType,
        INamedTypeSymbol? grandfatheredAttr)
    {
        var op = (IInvocationOperation)context.Operation;
        if (!WriteMethods.Contains(op.TargetMethod.Name))
            return;

        var topLevelType = context.ContainingSymbol.ContainingTopLevelType();
        if (IsEventRepository(topLevelType))
            return;

        var dbSetName = GetEventDbSetReceiverName(op.Instance, dbContextType);
        if (dbSetName is null)
            return;

        var typeName = topLevelType?.Name ?? "<unknown>";
        var severity = topLevelType is not null
            ? GrandfatheredCheck.EffectiveSeverity(topLevelType, grandfatheredAttr, DiagnosticId)
            : DiagnosticSeverity.Error;
        context.ReportDiagnostic(Diagnostic.Create(
            descriptor: Rule,
            location: op.Syntax.GetLocation(),
            effectiveSeverity: severity,
            additionalLocations: null,
            properties: null,
            messageArgs: [typeName, dbSetName]));
    }

    private static string? GetEventDbSetReceiverName(
        IOperation? receiver,
        INamedTypeSymbol dbContextType)
    {
        receiver = Unwrap(receiver);
        if (receiver is not IPropertyReferenceOperation propertyReference)
            return null;

        var property = propertyReference.Property;
        if (!EventDbSets.Contains(property.Name))
            return null;

        return SymbolEqualityComparer.Default.Equals(property.ContainingType, dbContextType)
            ? property.Name
            : null;
    }

    private static IOperation? Unwrap(IOperation? operation)
    {
        while (operation is IConversionOperation conversion)
            operation = conversion.Operand;

        return operation;
    }

    private static bool IsEventRepository(INamedTypeSymbol? type) =>
        type is not null &&
        string.Equals(type.ToDisplayString(), EventRepositoryFullName, StringComparison.Ordinal);
}
