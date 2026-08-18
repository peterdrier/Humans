using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Humans.Analyzers.Internal.Rules;

/// <summary>
/// HUM0032 — cross-section calls go through <c>I&lt;Section&gt;ServiceRead</c>. Injecting
/// another section's write-capable service interface is the exception and must say so with
/// <c>[CrossSectionWrite("reason")]</c>.
/// </summary>
/// <remarks>
/// The rule reads the declaration, not the usage: holding a write interface is the thing
/// being declared, whether or not this class happens to call a write method today.
/// </remarks>
internal static class CrossSectionReadRule
{
    public const string DiagnosticId = "HUM0032";

    private const string ReadInterfaceSuffix = "ServiceRead";
    private const string CrossSectionWriteAttributeFullName = "Humans.Base.Attributes.CrossSectionWriteAttribute";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: "Cross-section injection of a write service interface",
        messageFormat:
            "'{0}' (section '{1}') injects '{2}' from section '{3}'. Inject '{4}' instead, or "
            + "mark '{0}' [CrossSectionWrite(\"reason\")] if it genuinely writes there.",
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "The read interface is the default cross-section contract; the full interface "
            + "grants write access to another section's data. Where a write is genuinely "
            + "needed, prefer pulling in the owning section's UI component and letting it do "
            + "its own writing. See memory/architecture/section-read-write-split.md.");

    public static void Register(CompilationStartAnalysisContext context)
    {
        var writeAttr = context.Compilation.GetTypeByMetadataName(CrossSectionWriteAttributeFullName);
        var grandfatheredAttr = GrandfatheredCheck.Resolve(context.Compilation);

        context.RegisterSymbolAction(
            ctx => Analyze(ctx, writeAttr, grandfatheredAttr),
            SymbolKind.NamedType);
    }

    private static void Analyze(
        SymbolAnalysisContext context,
        INamedTypeSymbol? writeAttr,
        INamedTypeSymbol? grandfatheredAttr)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind != TypeKind.Class || type.IsAbstract)
            return;

        var callerSection = Sections.Of(type, Sections.ServiceNamespacePrefix);
        if (callerSection is null)
            return;

        if (writeAttr is not null
            && type.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, writeAttr)))
        {
            return;
        }

        var severity = GrandfatheredCheck.EffectiveSeverity(type, grandfatheredAttr, DiagnosticId);

        foreach (var ctor in type.InstanceConstructors)
        {
            foreach (var parameter in ctor.Parameters)
            {
                if (parameter.Type is not INamedTypeSymbol { TypeKind: TypeKind.Interface } fullInterface)
                    continue;

                var readBase = fullInterface.AllInterfaces.FirstOrDefault(
                    i => i.Name.EndsWith(ReadInterfaceSuffix, StringComparison.Ordinal));
                if (readBase is null)
                    continue;

                var dependencySection = Sections.Of(fullInterface, Sections.InterfaceNamespacePrefix);
                if (dependencySection is null
                    || string.Equals(dependencySection, callerSection, StringComparison.Ordinal))
                {
                    continue;
                }

                context.ReportDiagnostic(Diagnostic.Create(
                    descriptor: Rule,
                    location: parameter.Locations.Length > 0 ? parameter.Locations[0] : ctor.Locations[0],
                    effectiveSeverity: severity,
                    additionalLocations: null,
                    properties: null,
                    messageArgs:
                    [
                        type.Name,
                        callerSection,
                        fullInterface.Name,
                        dependencySection,
                        readBase.Name,
                    ]));
            }
        }
    }
}
