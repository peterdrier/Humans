using System.Collections.Immutable;
using Humans.Analyzers.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Humans.Analyzers;

/// <summary>
/// HUM0028 — every interface that extends
/// <see cref="Humans.Application.Interfaces.IInvalidator"/> is a new
/// cache-invalidator concept. A separate invalidator existing is itself a
/// smell — usually a cross-section write or a flush the owning section's
/// service + caching decorator should have absorbed. The marker makes the
/// family countable so it can be ratcheted toward zero.
/// <list type="bullet">
/// <item>Existing invalidator interfaces carry
/// <c>[Grandfathered("HUM0028", …)]</c> → diagnostic rides as
/// <see cref="DiagnosticSeverity.Warning"/>.</item>
/// <item>A new <c>*Invalidator</c> interface (no <c>[Grandfathered]</c>)
/// → <see cref="DiagnosticSeverity.Error"/>, failing the PR that adds it.
/// To add an invalidator, either delete an existing one or get Peter to
/// sign off on a new grandfather.</item>
/// </list>
/// <c>Directory.Build.props</c> includes <c>HUM0028</c> in
/// <c>WarningsNotAsErrors</c> so the grandfathered warnings don't break the
/// build; that entry's exit condition is "delete when the last
/// <c>[Grandfathered(\"HUM0028\")]</c> is gone."
/// </summary>
/// <remarks>
/// Runs in <c>Humans.Application</c> only. The Application compilation owns
/// every invalidator interface declaration. Diagnostic fires at the
/// interface declaration itself (not at consumers).
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class InvalidatorInterfaceRatchetAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0028";

    private const string InvalidatorMarkerFullName = "Humans.Application.Interfaces.IInvalidator";

    private static readonly LocalizableString Title =
        "Cache-invalidator interface count is ratcheted";

    private static readonly LocalizableString MessageFormat =
        "'{0}' extends IInvalidator — a new cache-invalidator concept. The *Invalidator family is being ratcheted toward zero (existing ones carry [Grandfathered(\"HUM0028\", …)]); adding a new one usually means a cross-section write or a flush the owning section's service + caching decorator should have absorbed. Delete an existing invalidator or get Peter to sign off on a new grandfather.";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "A standalone *Invalidator existing is itself the smell — same doctrine as " +
            "crosscut-purity: the count must ratchet down. The IInvalidator marker makes " +
            "the family countable. Existing interfaces carry " +
            "[Grandfathered(\"HUM0028\", …)] which downgrades to Warning for visibility; " +
            "new interfaces extending IInvalidator are an Error. The exit condition for " +
            "the WarningsNotAsErrors entry is when the last [Grandfathered(\"HUM0028\")] " +
            "is gone.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

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

        var invalidatorMarker = context.Compilation.GetTypeByMetadataName(InvalidatorMarkerFullName);
        if (invalidatorMarker is null)
            return;

        var grandfatheredAttr = GrandfatheredCheck.Resolve(context.Compilation);

        context.RegisterSymbolAction(
            ctx => AnalyzeNamedType(ctx, invalidatorMarker, grandfatheredAttr),
            SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(
        SymbolAnalysisContext context,
        INamedTypeSymbol invalidatorMarker,
        INamedTypeSymbol? grandfatheredAttr)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind != TypeKind.Interface)
            return;

        // Don't flag the marker itself.
        if (SymbolEqualityComparer.Default.Equals(type, invalidatorMarker))
            return;

        // Fire only on interfaces that directly declare IInvalidator in their
        // base list — extending an already-flagged grandparent shouldn't
        // double-report. Direct base interfaces only.
        var declaresMarker = false;
        foreach (var iface in type.Interfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(iface, invalidatorMarker))
            {
                declaresMarker = true;
                break;
            }
        }

        if (!declaresMarker)
            return;

        var severity = GrandfatheredCheck.EffectiveSeverity(type, grandfatheredAttr, DiagnosticId);

        var location = type.Locations.Length > 0 ? type.Locations[0] : Location.None;

        context.ReportDiagnostic(Diagnostic.Create(
            descriptor: Rule,
            location: location,
            effectiveSeverity: severity,
            additionalLocations: null,
            properties: null,
            messageArgs: type.Name));
    }
}
