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
