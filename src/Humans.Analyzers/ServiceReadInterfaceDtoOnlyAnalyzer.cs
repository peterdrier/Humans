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
/// Deliberately <em>not</em> widened to section assemblies, but the assembly
/// boundary only covers <em>two of the three</em> banned families. A moved
/// section publishes its read surface from a <c>Humans.&lt;Section&gt;.Contracts</c>
/// leaf that references neither the section's own project nor EF Core, so an
/// entity type and a <c>DbSet</c>/change-tracking type are genuinely unnameable
/// there. <c>IQueryable&lt;T&gt;</c> is <em>not</em>: it is a BCL type in
/// <c>System.Linq</c>, which <c>ImplicitUsings</c> imports into every project,
/// so <c>IQueryable&lt;SomeDto&gt;</c> on a moved section's read interface
/// compiles with nothing to stop it. That gap is tracked, not accepted — see
/// nobodies-collective/Humans#1040; do not close it by quietly widening this
/// analyzer, since scoping is Peter's call.
/// </para>
/// <para>
/// For the <c>I*ServiceRead</c> interfaces still under
/// <c>Humans.Application/Interfaces</c> there is no boundary at all:
/// <c>Humans.Application.csproj</c> references <c>Humans.Domain</c>, so widening
/// one of those to return an entity <em>or</em> an <c>IQueryable&lt;T&gt;</c>
/// compiles. This rule is what stops it. The set shrinks with every G5 peel —
/// deliberately not counted here, because a count in a comment goes stale on the
/// next move. When the last one leaves, retiring this rule still forfeits the
/// <c>IQueryable</c> check everywhere, so nobodies-collective/Humans#1040 has to land first.
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

    /// <summary>
    /// EF entity types that do not live under <see cref="DomainEntitiesNamespace"/> because
    /// ASP.NET Identity forced them onto a contracts leaf.
    /// </summary>
    /// <remarks>
    /// <c>User : IdentityUser&lt;Guid&gt;</c> is named by <c>Humans.UI</c> and ~48 files across
    /// Shell and twenty test projects, and Base cannot reference a section, so the nine
    /// Users/Profiles entities are public on <c>Humans.Users.Contracts</c> rather than internal
    /// in the section (nobodies-collective/Humans#866, G5 lane 2, PR B). A namespace-keyed
    /// entity test cannot see that — <c>*.Contracts</c> is where DTOs live — so the rule would
    /// have gone quiet for exactly the nine entities the read boundary exists to keep off
    /// <c>IUserServiceRead</c>. Named explicitly instead; the list shrinks to nothing when the
    /// entities are internalised (recorded in the lane 2 handoff).
    /// </remarks>
    private static readonly ImmutableHashSet<string> LeafResidentEntities =
        ImmutableHashSet.Create(
            System.StringComparer.Ordinal,
            "Humans.Users.Contracts.User",
            "Humans.Users.Contracts.UserEmail",
            "Humans.Users.Contracts.EventParticipation",
            "Humans.Users.Contracts.Profile",
            "Humans.Users.Contracts.ContactField",
            "Humans.Users.Contracts.ProfileLanguage",
            "Humans.Users.Contracts.VolunteerHistoryEntry",
            "Humans.Users.Contracts.CommunicationPreference",
            "Humans.Users.Contracts.AccountMergeRequest");
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

        if (LeafResidentEntities.Contains(type.ToDisplayString()))
            return true;

        if (IsInOrUnder(ns, EfCoreNamespace))
            return true;

        return false;
    }

    private static bool IsInOrUnder(string ns, string root) =>
        string.Equals(ns, root, System.StringComparison.Ordinal) ||
        ns.StartsWith(root + ".", System.StringComparison.Ordinal);
}
