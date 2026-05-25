using System;
using System.Collections.Immutable;
using Humans.Analyzers.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Humans.Analyzers;

/// <summary>
/// HUM0022 -- only NotificationRepository may write the Notifications section DbSets.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class NotificationDbSetWriteAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0022";

    private const string HumansDbContextFullName = "Humans.Infrastructure.Data.HumansDbContext";
    private const string NotificationRepositoryFullName =
        "Humans.Infrastructure.Repositories.Notifications.NotificationRepository";
    private const string NotificationEntityFullName = "Humans.Infrastructure.Data.Notification";
    private const string NotificationRecipientEntityFullName = "Humans.Infrastructure.Data.NotificationRecipient";
    private const string DbSetOpenTypeFullName = "Microsoft.EntityFrameworkCore.DbSet`1";

    private static readonly ImmutableHashSet<string> NotificationDbSets =
        ImmutableHashSet.Create(StringComparer.Ordinal, "Notifications", "NotificationRecipients");

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
        "Notification DbSets may only be written by NotificationRepository";

    private static readonly LocalizableString MessageFormat =
        "'{0}' writes HumansDbContext.{1}. Route notification writes through INotificationRepository or the Notifications application service boundary.";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "The Notifications section owns the notifications and notification_recipients tables. " +
            "Only NotificationRepository may call EF write methods on HumansDbContext.Notifications " +
            "or HumansDbContext.NotificationRecipients.");

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

        var dbSetOpenType = context.Compilation.GetTypeByMetadataName(DbSetOpenTypeFullName);
        var notificationEntityType = context.Compilation.GetTypeByMetadataName(NotificationEntityFullName);
        var notificationRecipientType = context.Compilation.GetTypeByMetadataName(NotificationRecipientEntityFullName);
        var grandfatheredAttr = GrandfatheredCheck.Resolve(context.Compilation);

        context.RegisterOperationAction(
            ctx => AnalyzeInvocation(
                ctx,
                dbContextType,
                dbSetOpenType,
                notificationEntityType,
                notificationRecipientType,
                grandfatheredAttr),
            OperationKind.Invocation);
    }

    private static void AnalyzeInvocation(
        OperationAnalysisContext context,
        INamedTypeSymbol dbContextType,
        INamedTypeSymbol? dbSetOpenType,
        INamedTypeSymbol? notificationEntityType,
        INamedTypeSymbol? notificationRecipientType,
        INamedTypeSymbol? grandfatheredAttr)
    {
        var op = (IInvocationOperation)context.Operation;
        if (!WriteMethods.Contains(op.TargetMethod.Name))
            return;

        var topLevelType = context.ContainingSymbol.ContainingTopLevelType();
        if (IsNotificationRepository(topLevelType))
            return;

        var dbSetName = GetNotificationDbSetReceiverName(
            op.Instance,
            dbContextType,
            dbSetOpenType,
            notificationEntityType,
            notificationRecipientType);
        if (dbSetName is null)
            return;

        var typeName = topLevelType?.Name ?? "<unknown>";
        var severity = topLevelType is null
            ? DiagnosticSeverity.Error
            : GrandfatheredCheck.EffectiveSeverity(topLevelType, grandfatheredAttr, DiagnosticId);

        context.ReportDiagnostic(Diagnostic.Create(
            descriptor: Rule,
            location: op.Syntax.GetLocation(),
            effectiveSeverity: severity,
            additionalLocations: null,
            properties: null,
            messageArgs: [typeName, dbSetName]));
    }

    private static string? GetNotificationDbSetReceiverName(
        IOperation? receiver,
        INamedTypeSymbol dbContextType,
        INamedTypeSymbol? dbSetOpenType,
        INamedTypeSymbol? notificationEntityType,
        INamedTypeSymbol? notificationRecipientType)
    {
        receiver = Unwrap(receiver);

        // Direct property reference: ctx.Notifications.Add(...)
        if (receiver is IPropertyReferenceOperation propertyReference)
        {
            var property = propertyReference.Property;
            if (NotificationDbSets.Contains(property.Name)
                && SymbolEqualityComparer.Default.Equals(property.ContainingType, dbContextType))
            {
                return property.Name;
            }
        }

        // Type-based fallback: a DbSet<Notification> captured into a local or field
        // (var set = ctx.Notifications; set.Add(...)) still routes a write through the
        // section's table, so match on the receiver's DbSet<T> element type.
        if (dbSetOpenType is not null
            && receiver?.Type is INamedTypeSymbol receiverType
            && SymbolEqualityComparer.Default.Equals(receiverType.OriginalDefinition, dbSetOpenType)
            && receiverType.TypeArguments.Length == 1
            && receiverType.TypeArguments[0] is INamedTypeSymbol elementType)
        {
            if (notificationEntityType is not null
                && SymbolEqualityComparer.Default.Equals(elementType, notificationEntityType))
            {
                return "Notifications";
            }

            if (notificationRecipientType is not null
                && SymbolEqualityComparer.Default.Equals(elementType, notificationRecipientType))
            {
                return "NotificationRecipients";
            }
        }

        return null;
    }

    private static IOperation? Unwrap(IOperation? operation)
    {
        while (operation is IConversionOperation conversion)
            operation = conversion.Operand;

        return operation;
    }

    private static bool IsNotificationRepository(INamedTypeSymbol? type) =>
        type is not null &&
        string.Equals(type.ToDisplayString(), NotificationRepositoryFullName, StringComparison.Ordinal);
}
