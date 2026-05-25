using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using Humans.Analyzers.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Humans.Analyzers;

/// <summary>
/// HUM0024 -- EF configurations should not create navigation joins across sections.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class CrossSectionEfJoinAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0024";

    private const string ConfigurationNamespacePrefix = "Humans.Infrastructure.Data.Configurations";
    private const string EntityTypeConfigurationFullName = "Microsoft.EntityFrameworkCore.IEntityTypeConfiguration`1";
    private const string EfBuilderNamespacePrefix = "Microsoft.EntityFrameworkCore";

    private static readonly ImmutableHashSet<string> RelationshipMethods =
        ImmutableHashSet.Create(StringComparer.Ordinal, "HasOne", "HasMany");

    private static readonly LocalizableString Title =
        "EF navigation join crosses section boundary";

    private static readonly LocalizableString MessageFormat =
        "'{0}' configures a {1} navigation from section '{2}' to '{3}' ({4}). Cross-section linkage must stay as a bare FK and be stitched through services.";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "A section's EF model must not configure HasOne/HasMany navigation joins to entities owned by " +
            "another section. Existing violators carry [Grandfathered(\"HUM0024\", ...)] until their joins " +
            "are migrated to bare FK columns and service-level stitching.");

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

        var entityTypeConfiguration = context.Compilation.GetTypeByMetadataName(EntityTypeConfigurationFullName);
        if (entityTypeConfiguration is null)
            return;

        var ownership = BuildOwnershipMap(context.Compilation, entityTypeConfiguration);
        if (ownership.EntitySections.Count == 0 || ownership.ConfigurationSections.Count == 0)
            return;

        var grandfatheredAttr = GrandfatheredCheck.Resolve(context.Compilation);

        context.RegisterOperationAction(
            ctx => AnalyzeInvocation(ctx, ownership, grandfatheredAttr),
            OperationKind.Invocation);
    }

    private static void AnalyzeInvocation(
        OperationAnalysisContext context,
        OwnershipMap ownership,
        INamedTypeSymbol? grandfatheredAttr)
    {
        var op = (IInvocationOperation)context.Operation;
        if (!RelationshipMethods.Contains(op.TargetMethod.Name))
            return;

        var methodNamespace = op.TargetMethod.ContainingNamespace?.ToDisplayString();
        if (methodNamespace is null ||
            !methodNamespace.StartsWith(EfBuilderNamespacePrefix, StringComparison.Ordinal))
        {
            return;
        }

        var configType = context.ContainingSymbol.ContainingTopLevelType();
        if (configType is null ||
            !ownership.ConfigurationSections.TryGetValue(SymbolKey(configType), out var thisSection))
        {
            return;
        }

        var target = FindTargetEntity(op, ownership.EntitySections);
        if (target is null)
            return;

        var targetKey = SymbolKey(target);
        if (!ownership.EntitySections.TryGetValue(targetKey, out var targetSection))
            return;

        if (string.Equals(thisSection, targetSection, StringComparison.Ordinal))
            return;

        var severity = GrandfatheredCheck.EffectiveSeverity(configType, grandfatheredAttr, DiagnosticId);
        context.ReportDiagnostic(Diagnostic.Create(
            descriptor: Rule,
            location: op.Syntax.GetLocation(),
            effectiveSeverity: severity,
            additionalLocations: null,
            properties: null,
            messageArgs: [configType.Name, op.TargetMethod.Name, thisSection, targetSection, target.Name]));
    }

    private static OwnershipMap BuildOwnershipMap(
        Compilation compilation,
        INamedTypeSymbol entityTypeConfiguration)
    {
        var configurationSections = new Dictionary<string, string>(StringComparer.Ordinal);
        var entitySections = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var type in EnumerateNamedTypes(compilation.GlobalNamespace))
        {
            var section = SectionFromConfigurationNamespace(type);
            if (section is null)
                continue;

            var configuredEntity = TryGetConfiguredEntity(type, entityTypeConfiguration);
            if (configuredEntity is null)
                continue;

            configurationSections[SymbolKey(type)] = section;
            var entityKey = SymbolKey(configuredEntity);
            if (!entitySections.ContainsKey(entityKey))
                entitySections[entityKey] = section;
        }

        return new OwnershipMap(configurationSections, entitySections);
    }

    private static INamedTypeSymbol? TryGetConfiguredEntity(
        INamedTypeSymbol type,
        INamedTypeSymbol entityTypeConfiguration)
    {
        foreach (var iface in type.AllInterfaces)
        {
            if (!SymbolEqualityComparer.Default.Equals(iface.OriginalDefinition, entityTypeConfiguration))
                continue;
            if (iface.TypeArguments.Length == 0)
                continue;
            return iface.TypeArguments[0] as INamedTypeSymbol;
        }

        return null;
    }

    private static string? SectionFromConfigurationNamespace(INamedTypeSymbol type)
    {
        var ns = type.ContainingNamespace?.ToDisplayString();
        if (ns is null || !ns.StartsWith(ConfigurationNamespacePrefix, StringComparison.Ordinal))
            return null;

        if (ns.Length == ConfigurationNamespacePrefix.Length)
            return null;

        if (ns[ConfigurationNamespacePrefix.Length] != '.')
            return null;

        var start = ConfigurationNamespacePrefix.Length + 1;
        var dot = ns.IndexOf('.', start);
        return dot < 0 ? ns.Substring(start) : ns.Substring(start, dot - start);
    }

    private static INamedTypeSymbol? FindTargetEntity(
        IInvocationOperation op,
        IReadOnlyDictionary<string, string> entitySections)
    {
        foreach (var typeArg in op.TargetMethod.TypeArguments)
        {
            if (entitySections.ContainsKey(SymbolKey(typeArg)))
                return typeArg as INamedTypeSymbol;
        }

        foreach (var arg in op.Arguments)
        {
            var property = FindFirstPropertyReference(arg.Value);
            var propertyType = property?.Property.Type as INamedTypeSymbol;
            if (propertyType is not null && entitySections.ContainsKey(SymbolKey(propertyType)))
                return propertyType;
        }

        return null;
    }

    private static IPropertyReferenceOperation? FindFirstPropertyReference(IOperation? operation)
    {
        if (operation is null)
            return null;

        if (operation is IPropertyReferenceOperation propertyReference)
            return propertyReference;

        foreach (var child in operation.ChildOperations)
        {
            var found = FindFirstPropertyReference(child);
            if (found is not null)
                return found;
        }

        return null;
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateNamedTypes(INamespaceSymbol ns)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            foreach (var t in EnumerateNamedTypes(type))
                yield return t;
        }

        foreach (var child in ns.GetNamespaceMembers())
        {
            foreach (var type in EnumerateNamedTypes(child))
                yield return type;
        }
    }

    private static IEnumerable<INamedTypeSymbol> EnumerateNamedTypes(INamedTypeSymbol type)
    {
        yield return type;

        foreach (var nested in type.GetTypeMembers())
        {
            foreach (var t in EnumerateNamedTypes(nested))
                yield return t;
        }
    }

    private static string SymbolKey(ITypeSymbol type) =>
        type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    private sealed class OwnershipMap
    {
        public OwnershipMap(
            IReadOnlyDictionary<string, string> configurationSections,
            IReadOnlyDictionary<string, string> entitySections)
        {
            ConfigurationSections = configurationSections;
            EntitySections = entitySections;
        }

        public IReadOnlyDictionary<string, string> ConfigurationSections { get; }
        public IReadOnlyDictionary<string, string> EntitySections { get; }
    }
}
