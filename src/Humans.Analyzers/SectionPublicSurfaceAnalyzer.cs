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
/// or model-snapshot type, a type the framework requires to be public in order
/// to function (view components and tag helpers), or declared under a
/// <c>Contracts/</c> path.
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
/// <b>The rule in one sentence: a section's types are <c>internal</c> by default,
/// except types the framework requires to be <c>public</c> in order to function.</b>
/// Those are two different kinds of exception and should not be confused. The
/// <c>Contracts/</c>, <c>ISection</c> and <c>&lt;Section&gt;Resource</c> carve-outs are
/// a <i>deliberate surface</i> — someone chose to let other assemblies depend on them.
/// The framework carve-out is not a choice at all: the type is public because it stops
/// working otherwise.
/// </para>
/// <para>
/// <b>The membership test for the framework exception: does making the type
/// <c>internal</c> fail loudly, or silently render nothing?</b> Silent means it
/// belongs in the exception. Anything Razor/MVC enumerates by reflection <i>at compile
/// time</i> qualifies, because those discovery passes filter on
/// <c>DeclaredAccessibility == Public</c> and simply skip what they cannot see — no
/// error, no warning, no diagnostic of any kind. Runtime resolution does not qualify:
/// it throws, so one render test catches it.
/// </para>
/// <para>
/// <b>Current membership: view components and tag helpers. That is the whole set
/// today</b> — a survey of this app's Razor/MVC discovery pipeline found no third
/// case. Controllers look like a candidate and are not: <c>MVC</c>'s
/// <c>SectionControllerFeatureProvider</c> makes internal ones route correctly, and a
/// missing controller 404s loudly. Both members are matched structurally, exactly the
/// way the corresponding discovery pass matches them, so this rule cannot drift out of
/// agreement with what actually gets discovered:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>View components</b> — <c>ViewComponentTagHelperDescriptorProvider</c> generates a
/// <c>&lt;vc:…&gt;</c> tag helper only from a <c>public</c> component, so an
/// <c>internal</c> one leaves the element to ship as inert literal markup: green build,
/// HTTP 200, nothing rendered. This is not hypothetical — it cost the whole Profile
/// page its <c>&lt;vc:profile-card&gt;</c> and no test noticed. Runtime lookup is a
/// different seam and is what hid it: <c>SectionViewComponentFeatureProvider</c>
/// registers internal components for <c>Component.InvokeAsync("Name")</c>, so nothing
/// ever threw. Matched like <c>ViewComponentConventions.IsComponent</c>.
/// </description></item>
/// <item><description>
/// <b>Tag helpers</b> — <c>DefaultTagHelperDescriptorProvider</c> applies the same
/// <c>public</c> filter, with the same silent outcome for the element. No section
/// declares one today (the repo's only tag helper is <c>Humans.UI</c>'s
/// <c>AuthorizeViewTagHelper</c>, already public in a horizontal), so this clause is
/// written ahead of its first subject rather than in response to a broken page —
/// deliberately, so the next lane reads a principle instead of inferring one from a
/// single carve-out. Matched by implementing <c>ITagHelper</c>, which is the whole of
/// that provider's test.
/// </description></item>
/// </list>
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
    private const string ViewComponentNameSuffix = "ViewComponent";
    private const string ViewComponentAttributeFullName = "Microsoft.AspNetCore.Mvc.ViewComponentAttribute";
    private const string NonViewComponentAttributeFullName = "Microsoft.AspNetCore.Mvc.NonViewComponentAttribute";
    private const string TagHelperInterfaceFullName = "Microsoft.AspNetCore.Razor.TagHelpers.ITagHelper";

    private static readonly LocalizableString Title =
        "Public type outside a section's public surface";

    private static readonly LocalizableString MessageFormat =
        "'{0}' is public in section assembly '{1}'. A section is internal by default: " +
        "its public surface is its Section entry point, its <Section>Resource marker, " +
        "EF migrations, types the framework requires to be public in order to function " +
        "(view components, tag helpers), and types under Contracts/. Make '{0}' " +
        "internal, or move it under Contracts/ if another section needs it.";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "A section assembly (nobodies-collective/Humans#866, G5) is internal by " +
            "default, with two kinds of exception: a deliberate surface other sections " +
            "and Shell may depend on (Contracts/, the Section entry point, the " +
            "<Section>Resource marker), and types the framework requires to be public " +
            "in order to function at all (view components, tag helpers — Razor's " +
            "compile-time discovery enumerates only public types, and an internal one " +
            "renders nothing rather than failing). Every other public type is either an " +
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
        var viewComponentAttr = context.Compilation.GetTypeByMetadataName(ViewComponentAttributeFullName);
        var nonViewComponentAttr = context.Compilation.GetTypeByMetadataName(NonViewComponentAttributeFullName);
        var tagHelperInterface = context.Compilation.GetTypeByMetadataName(TagHelperInterfaceFullName);
        var grandfatheredAttr = GrandfatheredCheck.Resolve(context.Compilation);

        context.RegisterSymbolAction(
            ctx => AnalyzeNamedType(
                ctx, sectionMarker, migrationBase, viewComponentAttr, nonViewComponentAttr,
                tagHelperInterface, grandfatheredAttr),
            SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(
        SymbolAnalysisContext context,
        INamedTypeSymbol? sectionMarker,
        INamedTypeSymbol? migrationBase,
        INamedTypeSymbol? viewComponentAttr,
        INamedTypeSymbol? nonViewComponentAttr,
        INamedTypeSymbol? tagHelperInterface,
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
        if (IsFrameworkRequiredPublic(type, viewComponentAttr, nonViewComponentAttr, tagHelperInterface))
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
    /// A type the framework requires to be <c>public</c> in order to function: Razor's
    /// compile-time discovery passes filter on public accessibility and silently skip
    /// what they cannot see, so <c>internal</c> here means "renders nothing", not
    /// "compile error". See the membership test in the remarks on this class.
    /// </summary>
    private static bool IsFrameworkRequiredPublic(
        INamedTypeSymbol type,
        INamedTypeSymbol? viewComponentAttr,
        INamedTypeSymbol? nonViewComponentAttr,
        INamedTypeSymbol? tagHelperInterface) =>
        IsViewComponent(type, viewComponentAttr, nonViewComponentAttr)
        || IsTagHelper(type, tagHelperInterface);

    /// <summary>
    /// An MVC view component. Matched exactly the way
    /// <c>ViewComponentConventions.IsComponent</c> matches it — a non-abstract,
    /// non-generic class whose name ends in "ViewComponent" or which carries
    /// <c>[ViewComponent]</c>, minus any type marked <c>[NonViewComponent]</c> — so
    /// the carve-out covers precisely the set MVC and Razor treat as components, and
    /// nothing that merely looks like one.
    /// </summary>
    private static bool IsViewComponent(
        INamedTypeSymbol type,
        INamedTypeSymbol? viewComponentAttr,
        INamedTypeSymbol? nonViewComponentAttr)
    {
        if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.IsGenericType)
            return false;
        if (HasAttribute(type, nonViewComponentAttr))
            return false;

        return type.Name.EndsWith(ViewComponentNameSuffix, System.StringComparison.Ordinal)
            || HasAttribute(type, viewComponentAttr);
    }

    /// <summary>
    /// A Razor tag helper. Matched exactly the way
    /// <c>DefaultTagHelperDescriptorProvider</c> matches it — a non-abstract,
    /// non-generic class implementing <c>ITagHelper</c>. Implementing the interface is
    /// the whole of that provider's test, so naming is irrelevant in both directions: a
    /// class called <c>SomethingTagHelper</c> that does not implement it is ordinary
    /// section internals and still reported, and a differently-named implementation is
    /// still carved out.
    /// </summary>
    private static bool IsTagHelper(INamedTypeSymbol type, INamedTypeSymbol? tagHelperInterface)
    {
        if (tagHelperInterface is null)
            return false;
        if (type.TypeKind != TypeKind.Class || type.IsAbstract || type.IsGenericType)
            return false;

        foreach (var iface in type.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(iface, tagHelperInterface))
                return true;
        }
        return false;
    }

    private static bool HasAttribute(INamedTypeSymbol type, INamedTypeSymbol? attribute) =>
        attribute is not null
        && type.GetAttributes().Any(a => SymbolEqualityComparer.Default.Equals(a.AttributeClass, attribute));

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
