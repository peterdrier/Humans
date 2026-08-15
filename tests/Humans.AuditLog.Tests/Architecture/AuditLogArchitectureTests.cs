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

    // Anchored on Section rather than IAuditLogRepository: Section is the ISection registration
    // and cannot leave Humans.AuditLog, so this anchor is immune by construction. A repository
    // interface anchor would silently retarget onto Humans.AuditLog.Contracts the day the
    // interface moves there, after which every sweep below goes near-empty and still passes.
    private static System.Reflection.Assembly SectionAssembly => typeof(Section).Assembly;

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

    // ── Retired: SectionReferencesNoVerticalSection ──────────────────────────
    //
    // This test pinned the referenced-assembly list of Humans.AuditLog to
    // ["Humans.Gdpr.Contracts", "Humans.Users.Contracts"], on the premise that AuditLog is a
    // horizontal section and therefore may not name a vertical one — which is what kept the
    // name-resolving read path (AuditViewerService, injecting ITeamServiceRead) in
    // Humans.Application.
    //
    // Peter reversed that premise in the Base-floor decision of 2026-08-14: a former Base
    // resident that names another section's read interface moves to its own section, and Base
    // gets no Humans.Teams.Contracts reference to keep it. AuditLog now takes Teams',
    // GoogleIntegration's and Users' contracts leaves, and the assertion asserts the opposite
    // of the decision. Retired deliberately rather than re-baselined: re-listing the three new
    // leaves would restate whatever the csproj happens to say, which is not an invariant.
    // (nobodies-collective/Humans#866, G5 lane 4b-2h.)
}
