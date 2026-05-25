using System.Collections.Immutable;
using Humans.Analyzers.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Humans.Analyzers;

/// <summary>
/// HUM0021 -- cross-section navigation properties marked obsolete must not be
/// read. Callers should carry IDs and stitch display data through section
/// service read interfaces instead.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ObsoleteCrossDomainNavReadAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0021";

    private static readonly LocalizableString Title =
        "Cross-domain navigation property must not be read";

    private static readonly LocalizableString MessageFormat =
        "Read of obsolete cross-domain navigation property '{0}.{1}'. " +
        "Use the stored ID and resolve cross-section data through the owning service read interface.";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description:
            "Cross-domain EF navigation properties are marked [Obsolete(\"Cross-domain nav...\")]. " +
            "Application, Web, and Infrastructure code must not read them; resolve by ID through " +
            "the owning section service/read interface.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    private const string ObsoleteAttributeFullName = "System.ObsoleteAttribute";
    private const string CrossDomainNavMarker = "Cross-domain nav";
    private const string InfrastructureConfigurationNamespacePrefix =
        "Humans.Infrastructure.Data.Configurations";

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        if (!AssemblyScope.IsApplicationWebOrInfrastructure(context.Compilation.Assembly))
            return;

        context.RegisterOperationAction(AnalyzePropertyReference, OperationKind.PropertyReference);
    }

    private static void AnalyzePropertyReference(OperationAnalysisContext context)
    {
        var op = (IPropertyReferenceOperation)context.Operation;
        if (IsAssignmentTarget(op) || IsInsideNameOf(op))
            return;

        var containingType = context.ContainingSymbol.ContainingTopLevelType();
        if (IsInfrastructureEfConfiguration(containingType))
            return;

        var prop = op.Property;
        if (!IsObsoleteCrossDomainNavigation(prop))
            return;

        var typeName = prop.ContainingType?.Name ?? "<unknown>";
        context.ReportDiagnostic(Diagnostic.Create(
            Rule,
            op.Syntax.GetLocation(),
            typeName,
            prop.Name));
    }

    private static bool IsObsoleteCrossDomainNavigation(IPropertySymbol prop)
    {
        foreach (var attr in prop.GetAttributes())
        {
            if (!string.Equals(
                    attr.AttributeClass?.ToDisplayString(),
                    ObsoleteAttributeFullName,
                    System.StringComparison.Ordinal))
            {
                continue;
            }

            if (attr.ConstructorArguments.Length == 0)
                continue;

            var message = attr.ConstructorArguments[0].Value as string;
            if (message is not null &&
                message.IndexOf(CrossDomainNavMarker, System.StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsAssignmentTarget(IOperation op)
    {
        var parent = op.Parent;
        return parent switch
        {
            ISimpleAssignmentOperation sa => ReferenceEquals(sa.Target, op),
            ICompoundAssignmentOperation ca => ReferenceEquals(ca.Target, op),
            ICoalesceAssignmentOperation coa => ReferenceEquals(coa.Target, op),
            _ => false
        };
    }

    private static bool IsInsideNameOf(IOperation op)
    {
        for (var current = op.Parent; current is not null; current = current.Parent)
        {
            if (current.Kind == OperationKind.NameOf)
                return true;
        }

        return false;
    }

    private static bool IsInfrastructureEfConfiguration(INamedTypeSymbol? type)
    {
        if (type is null)
            return false;

        var ns = type.ContainingNamespace?.ToDisplayString();
        return ns is not null &&
            ns.StartsWith(InfrastructureConfigurationNamespacePrefix, System.StringComparison.Ordinal);
    }
}
