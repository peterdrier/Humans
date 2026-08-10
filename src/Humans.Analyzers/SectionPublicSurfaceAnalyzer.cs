using System.Collections.Immutable;
using System.Linq;
using Humans.Analyzers.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Humans.Analyzers;

/// <summary>
/// HUM0034 — In an assembly carrying <c>[assembly: Section("…")]</c>
/// (nobodies-collective/Humans#866, G5), a <c>public</c> type is an error
/// unless it is the section's <c>ISection</c> entry point, its
/// <c>&lt;Section&gt;Resource</c> localization marker, an EF Core migration
/// or model-snapshot type, or declared under a <c>Contracts/</c> path.
/// </summary>
/// <remarks>
/// <para>
/// #866's core rule — <c>internal</c> by default, public surface confined to
/// <c>Contracts/</c> — was convention-only across the first five sections that
/// moved (Store, SystemSettings, Events, Containers, Finance). Nothing stopped
/// the next PR making a section type <c>public</c>; this analyzer is the
/// keystone that makes the convention load-bearing (nobodies-collective/Humans#1013,
/// design §10).
/// </para>
/// <para>
/// The <c>ISection</c> and <c>&lt;Section&gt;Resource</c> carve-outs are matched the
/// same way runtime discovery matches them
/// (<see cref="Humans.Web.Extensions.SectionDiscoveryExtensions"/>): by implementing
/// <c>ISection</c>, and by a class name ending in <c>Resource</c>, respectively —
/// not by a fixed path — so the analyzer can never disagree with what boot actually
/// discovers.
/// </para>
/// <para>
/// EF Core's migration scaffolder always emits <c>public partial class : Migration</c>
/// for a baseline/incremental migration (not configurable, and hand-editing a
/// generated migration is forbidden project-wide) — every section's baseline
/// migration would otherwise fail this rule on day one. Matched structurally by base
/// type, the same way <c>Internal/SectionDbContexts.cs</c> matches "application
/// DbContext" for HUM0008/09/25/26, so moving migrations cannot silently defeat it.
/// </para>
/// <para>
/// <b>Checked on declared accessibility, not effective (externally-reachable)
/// accessibility.</b> A <c>public</c> type nested inside an already-<c>internal</c>
/// container isn't actually exported today — <c>GetExportedTypes()</c> wouldn't
/// return it — but the rule still fires on it. The declaration itself is the
/// signal being policed: an unnecessary <c>public</c> modifier is a landmine, not a
/// currently-harmless no-op — the moment its container (or the type itself, if
/// later hoisted to top level) flips to <c>public</c>, it exports silently, with no
/// second review of the nested member's own accessibility. Reviewing the modifier
/// at declaration time is cheaper than re-auditing every nested member on every
/// future container-visibility change.
/// </para>
/// <para>
/// <b>HUM0034 violations are fixed, never grandfathered.</b>
/// <c>[Grandfathered]</c>'s <c>AttributeUsage</c> covers classes, interfaces and
/// methods only, so a public <c>struct</c>, <c>record struct</c>, <c>enum</c> or
/// <c>delegate</c> — all of which this rule reports, since it runs over every
/// <see cref="SymbolKind.NamedType"/> — cannot carry the attribute (CS0592). That
/// is deliberate and not a gap to close by widening the attribute: unlike the
/// cross-section rules it was built for, every HUM0034 violation has a fix that is
/// always available and always local — drop the <c>public</c> modifier, or move the
/// type under <c>Contracts/</c> if another section genuinely needs it. Neither
/// requires restructuring, so there is no violation this rule can raise that has to
/// be deferred. Grandfathering exists for debt that cannot be paid down today;
/// deadline pressure alone does not qualify it
/// (<c>docs/architecture/peters-hard-rules.md</c>: "fix it right, or record an
/// issue"). The <c>GrandfatheredCheck</c> call below is retained for consistency
/// with every other HUM rule and to keep class/interface violators unblockable in
/// an emergency — it is not an invitation.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SectionPublicSurfaceAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0034";

    private const string ISectionFullName = "Humans.Application.Interfaces.ISection";
    private const string EfMigrationFullName = "Microsoft.EntityFrameworkCore.Migrations.Migration";
    private const string ResourceNameSuffix = "Resource";
    private const string ContractsPathSegment = "/Contracts/";

    private static readonly LocalizableString Title =
        "Public type outside a section's public surface";

    private static readonly LocalizableString MessageFormat =
        "'{0}' is public in section assembly '{1}'. A section's only public surface " +
        "is its Section entry point, its <Section>Resource marker, EF migrations, and " +
        "types under Contracts/. Make '{0}' internal, or move it under Contracts/ if " +
        "another section needs it.";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "A section assembly (nobodies-collective/Humans#866, G5) is internal by " +
            "default: public surface confined to Contracts/ is the boundary other " +
            "sections and Shell may depend on. Every other public type is either an " +
            "accident waiting to be depended on, or belongs under Contracts/ instead " +
            "(design §10, §6a).");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        if (!AssemblyScope.IsSection(context.Compilation.Assembly))
            return;

        var sectionMarker = context.Compilation.GetTypeByMetadataName(ISectionFullName);
        var migrationBase = context.Compilation.GetTypeByMetadataName(EfMigrationFullName);
        var grandfatheredAttr = GrandfatheredCheck.Resolve(context.Compilation);

        context.RegisterSymbolAction(
            ctx => AnalyzeNamedType(ctx, sectionMarker, migrationBase, grandfatheredAttr),
            SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(
        SymbolAnalysisContext context,
        INamedTypeSymbol? sectionMarker,
        INamedTypeSymbol? migrationBase,
        INamedTypeSymbol? grandfatheredAttr)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.DeclaredAccessibility != Accessibility.Public)
            return;
        if (type.IsImplicitlyDeclared)
            return;

        if (IsSectionEntryPoint(type, sectionMarker))
            return;
        if (IsResourceMarker(type))
            return;
        if (IsEfMigration(type, migrationBase))
            return;
        if (IsUnderContracts(type))
            return;

        var location = type.Locations.Length > 0 ? type.Locations[0] : Location.None;
        var severity = GrandfatheredCheck.EffectiveSeverity(type, grandfatheredAttr, DiagnosticId);
        context.ReportDiagnostic(Diagnostic.Create(
            descriptor: Rule,
            location: location,
            effectiveSeverity: severity,
            additionalLocations: null,
            properties: null,
            messageArgs: [type.Name, type.ContainingAssembly.Name]));
    }

    /// <summary>
    /// <c>public sealed class Section : ISection</c> at the project root — matched by
    /// implementing <see cref="ISectionFullName"/>, the exact test runtime discovery
    /// uses (<c>typeof(ISection).IsAssignableFrom(t)</c>), not by the name "Section".
    /// </summary>
    private static bool IsSectionEntryPoint(INamedTypeSymbol type, INamedTypeSymbol? sectionMarker)
    {
        if (sectionMarker is null)
            return false;
        if (type.TypeKind != TypeKind.Class || type.IsAbstract)
            return false;

        foreach (var iface in type.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(iface, sectionMarker))
                return true;
        }
        return false;
    }

    /// <summary>
    /// The <c>&lt;Section&gt;Resource</c> localization marker — matched the same way
    /// <c>SectionDiscoveryExtensions.SectionResourceTypes()</c> matches it at runtime:
    /// a non-abstract class whose name ends in "Resource".
    /// </summary>
    private static bool IsResourceMarker(INamedTypeSymbol type) =>
        type.TypeKind == TypeKind.Class
        && !type.IsAbstract
        && type.Name.EndsWith(ResourceNameSuffix, System.StringComparison.Ordinal);

    /// <summary>
    /// An EF Core migration (<c>BaselineXxx : Migration</c>). The scaffolder always
    /// emits these <c>public</c>, and migrations may not be hand-edited to change
    /// that (memory/process/never-hand-edit-migrations), so this is structural, not
    /// optional. <c>ModelSnapshot</c> types are unaffected — the scaffolder already
    /// emits those without an accessibility modifier (internal).
    /// </summary>
    private static bool IsEfMigration(INamedTypeSymbol type, INamedTypeSymbol? migrationBase) =>
        migrationBase is not null && type.InheritsFromOrEquals(migrationBase.ToDisplayString());

    /// <summary>
    /// True when <paramref name="type"/> is declared under a <c>Contracts/</c>
    /// path — the carve-out with no structural identity of its own (a DTO or
    /// interface under <c>Contracts/</c> looks like any other DTO or interface).
    /// </summary>
    /// <remarks>
    /// Namespace first, same as HUM0012/HUM0013: folder <c>Contracts/</c> maps to
    /// a namespace segment named <c>Contracts</c> by the same convention those
    /// rules already lean on, and it needs no file path to check (real code or
    /// test). Falls back to the declaring file's path for the case that
    /// convention misses — the same dual check <c>ConcurrencyTokenAnalyzer</c>'s
    /// <c>IsInMigration</c> uses for its own folder carve-out.
    /// </remarks>
    private static bool IsUnderContracts(INamedTypeSymbol type)
    {
        var ns = type.ContainingNamespace?.ToDisplayString();
        if (ns is not null && ns.Split('.').Any(segment => string.Equals(segment, "Contracts", System.StringComparison.Ordinal)))
            return true;

        foreach (var syntaxRef in type.DeclaringSyntaxReferences)
        {
            var filePath = syntaxRef.SyntaxTree.FilePath;
            if (string.IsNullOrEmpty(filePath))
                continue;

            var normalized = "/" + filePath.Replace('\\', '/').TrimStart('/') + "/";
            if (normalized.IndexOf(ContractsPathSegment, System.StringComparison.Ordinal) >= 0)
                return true;
        }
        return false;
    }
}
