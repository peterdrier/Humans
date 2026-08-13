using Humans.AuditLog.Data;
using AwesomeAssertions;

namespace Humans.AuditLog.Tests.Architecture;

/// <summary>
/// Architecture tests enforcing section-specific invariants for the Audit
/// Log section.
///
/// <para>
/// Audit Log chose <b>Option A</b> (no caching decorator, no dict cache).
/// Writes are scattered across every section (~96 call sites) and reads are
/// admin-only, so a cache is not warranted — same rationale used by Users
/// (#243), Governance (#242), Budget (#544), and City Planning (#543) when
/// they skipped the decorator.
/// </para>
///
/// <para>
/// Generic cross-section invariants (sealed repos, no DbContext in services,
/// no IMemoryCache unless allowlisted, namespace placement) are covered by
/// the generic rules in <c>Architecture/Rules/</c> and are not repeated here.
/// </para>
///
/// <para>
/// <c>audit_log</c> is append-only per design-rules §12 — the repository
/// exposes only <c>AddAsync</c> for mutations; no <c>UpdateAsync</c> or
/// <c>DeleteAsync</c> surface is allowed. The architecture test
/// <see cref="IAuditLogRepository_HasNoUpdateOrDeleteMethods"/> pins that
/// constraint.
/// </para>
/// </summary>
public class AuditLogArchitectureTests
{
    // ── IAuditLogRepository ──────────────────────────────────────────────────

    [HumansFact]
    public void IAuditLogRepository_HasNoUpdateOrDeleteMethods()
    {
        // audit_log is append-only per design-rules §12.
        // The repository must not expose any UpdateAsync/DeleteAsync/RemoveAsync surface.
        var methods = typeof(IAuditLogRepository).GetMethods().Select(m => m.Name).ToList();

        methods.Should().NotContain(
            m => m.StartsWith("Update", StringComparison.Ordinal)
                 || m.StartsWith("Delete", StringComparison.Ordinal)
                 || m.StartsWith("Remove", StringComparison.Ordinal),
            because: "audit_log is append-only (§12); repositories for append-only tables expose only Add/Get methods");
    }

    // ── Section boundary (G5, nobodies-collective/Humans#866) ────────────────

    private static System.Reflection.Assembly SectionAssembly => typeof(IAuditLogRepository).Assembly;

    [HumansFact]
    public void SectionTypesTakeNoStringLocalizer()
    {
        // The section deliberately ships no Resources/ folder and no AuditLogResource:
        // its two pages are admin-only English and carry no Localizer[...] call
        // (G5-SECTION-TEMPLATE.md step 3b's first question). Assert it structurally so the
        // day someone adds copy the build says "carve a resource set first" rather than
        // silently resolving against Humans.UI's SharedResource.
        var offenders = SectionAssembly
            .GetTypes()
            .SelectMany(t => t.GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Concat(t.GetMethods().SelectMany(m => m.GetParameters()))
                .Select(param => (Type: t, param.ParameterType)))
            .Where(x => x.ParameterType.IsGenericType
                        && string.Equals(
                            x.ParameterType.GetGenericTypeDefinition().FullName,
                            "Microsoft.Extensions.Localization.IStringLocalizer`1",
                            StringComparison.Ordinal))
            .Select(x => $"{x.Type.FullName} takes {x.ParameterType.Name}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            because: "AuditLog has no resource set; a localizer here means copy was added without carving one");
    }

    [HumansFact]
    public void SectionServicesTakeNoDbContext()
    {
        // Restates the old "GetReferencedAssemblies() does not contain EntityFrameworkCore"
        // assertion, which stops meaning anything once the repository ships in the same
        // assembly as the service (G5-SECTION-TEMPLATE.md step 11). The real invariant is
        // that only the repository touches a context.
        var offenders = SectionAssembly
            .GetTypes()
            .Where(t => t.IsClass && t.Namespace?.StartsWith("Humans.AuditLog.Services", StringComparison.Ordinal) == true)
            .SelectMany(t => t.GetConstructors().SelectMany(c => c.GetParameters()).Select(param => (Type: t, param.ParameterType)))
            .Where(x => typeof(Microsoft.EntityFrameworkCore.DbContext).IsAssignableFrom(x.ParameterType)
                        || (x.ParameterType.IsGenericType
                            && x.ParameterType.GetGenericTypeDefinition() == typeof(Microsoft.EntityFrameworkCore.IDbContextFactory<>)))
            .Select(x => $"{x.Type.FullName} takes {x.ParameterType.Name}")
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        offenders.Should().BeEmpty(
            because: "only AuditLogRepository may touch AuditLogDbContext (peters-hard-rules.md)");
    }

    [HumansFact]
    public void SectionReferencesNoVerticalSection()
    {
        // AuditLog is a *horizontal* section. peters-hard-rules.md: horizontals "are
        // strictly forbidden from referencing vertical sections ... as that will cause
        // loops in the call graph". The referenced-assembly list is where that stops being
        // a convention: the only section assembly AuditLog may name is Gdpr's leaf, which
        // is itself horizontal. This is what keeps the name-resolving read path
        // (AuditViewerService, which injects ITeamServiceRead) in Humans.Application.
        var sectionRefs = SectionAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.StartsWith("Humans.", StringComparison.Ordinal))
            .Where(n => n is not ("Humans.Interfaces" or "Humans.Domain" or "Humans.Application"
                                 or "Humans.Infrastructure" or "Humans.UI" or "Humans.Analyzers"
                                 or "Humans.AuditLog.Contracts"))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        // Humans.Users.Contracts is the second entry, and it is not new coupling: AuditLogService
        // has injected IUserServiceRead all along, under the [CrossSectionException] on that class
        // ("Audit (crosscut) reads merged-account source IDs via IUserServiceRead"). The reference
        // only became visible here when the interface left Humans.Application for Users' contracts
        // leaf (nobodies-collective/Humans#866, lane 2 PR A). #866 names User/UserInfo as sanctioned
        // shared contracts; where they finally live — this leaf or the Base floor — is the lane 4
        // "what is Base" question. Deleting the injection is the only thing that removes this row.
        sectionRefs.Should().BeEquivalentTo(
            ["Humans.Gdpr.Contracts", "Humans.Users.Contracts"],
            because: "a horizontal section may reference only Base, other horizontals, and the sanctioned User/UserInfo contracts");
    }
}
