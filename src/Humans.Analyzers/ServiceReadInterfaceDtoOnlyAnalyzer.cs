using System.Collections.Immutable;
using Humans.Analyzers.Internal;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Humans.Analyzers;

/// <summary>
/// HUM0029 — Cross-section read interfaces (<c>I*Read</c>) must expose
/// DTO/Info projections only. EF entity types
/// (<c>Humans.Domain.Entities.*</c>), EF framework types
/// (<c>Microsoft.EntityFrameworkCore.*</c>, including <c>DbSet&lt;&gt;</c> and
/// change-tracking entries), and <c>System.Linq.IQueryable</c>/
/// <c>IQueryable&lt;T&gt;</c> may not appear in any method signature (return
/// type or parameters), at any depth of generic nesting or array element.
/// </summary>
/// <remarks>
/// <para>
/// Trigger: an interface declared in the <c>Humans.Application</c> assembly
/// whose name ends with <c>Read</c>. Mirrors the <c>I&lt;Section&gt;ServiceRead</c>
/// pattern in <c>memory/architecture/section-read-write-split.md</c>.
/// </para>
/// <para>
/// Exposing an entity through the read surface couples the consuming section
/// to the owning section's storage shape, defeating cross-section nav-strip
/// work. If a consumer needs entity-shaped data, the section's projection
/// is missing a field — fix the projection, don't widen the read interface.
/// </para>
/// <para>
/// Grandfathering: an interface carrying
/// <c>[Grandfathered("HUM0029", …)]</c> downgrades to a Warning so existing
/// drift can be ratcheted out.
/// </para>
/// </remarks>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class ServiceReadInterfaceDtoOnlyAnalyzer : DiagnosticAnalyzer
{
    public const string DiagnosticId = "HUM0029";

    private const string DomainEntitiesNamespace = "Humans.Domain.Entities";
    private const string EfCoreNamespace = "Microsoft.EntityFrameworkCore";
    private const string SystemLinqNamespace = "System.Linq";
    private const string QueryableName = "IQueryable";

    private static readonly LocalizableString Title =
        "Read interface exposes an EF type";

    private static readonly LocalizableString MessageFormat =
        "'{0}.{1}' exposes EF type '{2}'. Cross-section read interfaces (I*Read) must use DTO/Info projections only — no EF entities, no Microsoft.EntityFrameworkCore types, no IQueryable. See memory/architecture/section-read-write-split.md.";

    public static readonly DiagnosticDescriptor Rule = new(
        id: DiagnosticId,
        title: Title,
        messageFormat: MessageFormat,
        category: AnalyzerCategories.Architecture,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description:
            "I*Read interfaces are the cross-section consumption surface for a " +
            "section's service. They must expose only DTO/Info projections owned " +
            "by the section — never EF entity types from Humans.Domain.Entities, " +
            "Microsoft.EntityFrameworkCore types (DbSet, change-tracking, etc.), " +
            "or System.Linq.IQueryable. If a consumer needs an entity-shaped " +
            "field, add it to the projection.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [Rule];

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(OnCompilationStart);
    }

    private static void OnCompilationStart(CompilationStartAnalysisContext context)
    {
        if (!string.Equals(
                context.Compilation.Assembly.Name,
                AssemblyScope.Application,
                System.StringComparison.Ordinal))
        {
            return;
        }

        var grandfatheredAttr = GrandfatheredCheck.Resolve(context.Compilation);

        context.RegisterSymbolAction(
            ctx => AnalyzeNamedType(ctx, grandfatheredAttr),
            SymbolKind.NamedType);
    }

    private static void AnalyzeNamedType(
        SymbolAnalysisContext context,
        INamedTypeSymbol? grandfatheredAttr)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind != TypeKind.Interface)
            return;

        // Pattern is I<Foo>Read (ICampServiceRead, IUserServiceRead, …). Require
        // at least one character between the leading "I" and the trailing "Read"
        // so a hypothetical bare "IRead" marker wouldn't qualify.
        if (type.Name.Length < 6)
            return;
        if (!type.Name.EndsWith("Read", System.StringComparison.Ordinal))
            return;

        var severity = GrandfatheredCheck.EffectiveSeverity(type, grandfatheredAttr, DiagnosticId);

        foreach (var member in type.GetMembers())
        {
            if (member is not IMethodSymbol method)
                continue;
            if (method.MethodKind != MethodKind.Ordinary)
                continue;

            var offender = FindFirstBanned(method.ReturnType);
            foreach (var parameter in method.Parameters)
            {
                if (offender is not null) break;
                offender = FindFirstBanned(parameter.Type);
            }

            if (offender is null)
                continue;

            var location = method.Locations.Length > 0 ? method.Locations[0] : Location.None;
            context.ReportDiagnostic(Diagnostic.Create(
                descriptor: Rule,
                location: location,
                effectiveSeverity: severity,
                additionalLocations: null,
                properties: null,
                messageArgs: [type.Name, method.Name, offender.ToDisplayString()]));
        }
    }

    private static ITypeSymbol? FindFirstBanned(ITypeSymbol? type)
    {
        if (type is null)
            return null;

        if (IsBannedType(type))
            return type;

        if (type is IArrayTypeSymbol array)
            return FindFirstBanned(array.ElementType);

        if (type is INamedTypeSymbol named)
        {
            foreach (var arg in named.TypeArguments)
            {
                var hit = FindFirstBanned(arg);
                if (hit is not null)
                    return hit;
            }
        }

        return null;
    }

    private static bool IsBannedType(ITypeSymbol type)
    {
        var ns = type.ContainingNamespace?.ToDisplayString() ?? string.Empty;

        if (string.Equals(ns, SystemLinqNamespace, System.StringComparison.Ordinal) &&
            string.Equals(type.Name, QueryableName, System.StringComparison.Ordinal))
        {
            return true;
        }

        if (IsInOrUnder(ns, DomainEntitiesNamespace))
            return true;

        if (IsInOrUnder(ns, EfCoreNamespace))
            return true;

        return false;
    }

    private static bool IsInOrUnder(string ns, string root) =>
        string.Equals(ns, root, System.StringComparison.Ordinal) ||
        ns.StartsWith(root + ".", System.StringComparison.Ordinal);
}
