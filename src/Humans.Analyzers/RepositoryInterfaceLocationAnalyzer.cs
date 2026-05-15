using System.Collections.Immutable;
using Humans.Analyzers.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Humans.Analyzers;

/// <summary>
/// HUM0013 — Every interface that extends
/// <c>Humans.Application.Interfaces.Repositories.IRepository</c> (transitively)
/// must be declared in the <c>Humans.Application.Interfaces.Repositories</c>
/// namespace exactly (the repo interface folder is flat, not per-section).
/// </summary>
/// <remarks>
/// Runs in <c>Humans.Application</c> only. Replaces ~20 per-section reflection
/// assertions of the form
/// <c>IXxxRepository_LivesInApplicationInterfacesRepositoriesNamespace</c>.
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RepositoryInterfaceLocationAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0013";

    private const string ExpectedNamespace = "Humans.Application.Interfaces.Repositories";
    private const string IRepositoryFullName = "Humans.Application.Interfaces.Repositories.IRepository";

    private static readonly LocalizableString Title =
        "Repository interface is outside Humans.Application.Interfaces.Repositories";

    private static readonly LocalizableString MessageFormat =
        "'{0}' extends IRepository but lives in '{1}'. Declare it in {2}.";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "Repository interfaces (transitive extenders of IRepository) live in the flat " +
            ExpectedNamespace + " namespace per design-rules §3. The implementation lives " +
            "in Humans.Infrastructure.Repositories.<Section>; the interface stays in the " +
            "shared marker namespace so the Application layer reaches every repository the " +
            "same way.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(Rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        if (!string.Equals(context.Compilation.Assembly.Name, AssemblyScope.Application, System.StringComparison.Ordinal))
            return;

        var marker = context.Compilation.GetTypeByMetadataName(IRepositoryFullName);
        if (marker is null)
            return;

        var grandfatheredAttr = GrandfatheredCheck.Resolve(context.Compilation);

        context.RegisterSymbolAction(
            ctx => AnalyzeNamedType(ctx, marker, grandfatheredAttr),
            SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(
        SymbolAnalysisContext context,
        INamedTypeSymbol marker,
        INamedTypeSymbol? grandfatheredAttr)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind != TypeKind.Interface)
            return;

        // The marker itself sits in the expected namespace; don't flag it.
        if (SymbolEqualityComparer.Default.Equals(type, marker))
            return;

        if (!ExtendsMarker(type, marker))
            return;

        var ns = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;
        if (string.Equals(ns, ExpectedNamespace, System.StringComparison.Ordinal))
            return;

        var location = type.Locations.Length > 0 ? type.Locations[0] : Location.None;
        var severity = GrandfatheredCheck.EffectiveSeverity(type, grandfatheredAttr, DiagnosticId);
        context.ReportDiagnostic(Diagnostic.Create(
            descriptor: Rule,
            location: location,
            effectiveSeverity: severity,
            additionalLocations: null,
            properties: null,
            messageArgs: new object[] { type.Name, ns, ExpectedNamespace }));
    }

    private static bool ExtendsMarker(INamedTypeSymbol type, INamedTypeSymbol marker)
    {
        foreach (var iface in type.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(iface, marker))
                return true;
        }
        return false;
    }
}
