using System.Text.RegularExpressions;
using AwesomeAssertions;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Tests.Architecture.Ratchet;
using Humans.Infrastructure.Repositories.AuditLog;
using Xunit;

namespace Humans.Application.Tests.Architecture;

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

    // ── Sole-writer DbSet rule ───────────────────────────────────────────────

    /// <summary>
    /// Only <c>AuditLogRepository</c> may write to <c>ctx.AuditLogEntries</c>.
    /// Any other production class that calls <c>.Add</c>, <c>.AddRange</c>,
    /// <c>.Update</c>, <c>.Remove</c>, or <c>.Attach</c> on
    /// <c>AuditLogEntries</c> is a cross-section boundary violation.
    ///
    /// <para>
    /// Current known violation: <c>DriveActivityMonitorRepository.PersistAnomaliesAsync</c>
    /// calls <c>ctx.AuditLogEntries.AddRange(anomalies)</c> directly. This is
    /// baselining until the GoogleIntegration /section-align run switches to
    /// calling <c>IAuditLogService.LogAsync</c> per anomaly.
    /// </para>
    /// </summary>
    [HumansFact]
    public void Only_AuditLogRepository_Writes_AuditLogEntries_DbSet()
    {
        var repoRoot = RatchetTestRunner.LocateRepoRoot();
        var violations = ScanAuditLogEntriesWrites(repoRoot);
        RatchetTestRunner.Run(
            "OnlyAuditLogRepositoryWritesAuditLogEntries",
            "tests/Humans.Application.Tests/Architecture/Baselines/OnlyAuditLogRepositoryWritesAuditLogEntries.baseline.txt",
            violations);
    }

    // Matches the write-operation call chains on the AuditLogEntries DbSet.
    // e.g. ctx.AuditLogEntries.Add(...)  /  .AddRange  /  .Update  /  .Remove  /  .Attach
    private static readonly Regex AuditLogWriteRegex = new(
        @"AuditLogEntries\s*\.\s*(?:Add|AddRange|Update|Remove|Attach)\b",
        RegexOptions.Compiled | RegexOptions.ExplicitCapture,
        TimeSpan.FromSeconds(2));

    internal static IEnumerable<string> ScanAuditLogEntriesWrites(string repoRoot)
    {
        foreach (var path in RatchetTestRunner.EnumerateSourceFiles(repoRoot))
        {
            // The canonical owner is AuditLogRepository — exclude it from violation reporting.
            if (path.Replace('\\', '/').EndsWith(
                    "Infrastructure/Repositories/AuditLog/AuditLogRepository.cs",
                    StringComparison.Ordinal))
                continue;

            var content = File.ReadAllText(path);
            if (!AuditLogWriteRegex.IsMatch(content)) continue;

            var rel = RatchetTestRunner.ToRelativePath(repoRoot, path);
            var ordinal = 0;
            foreach (var match in AuditLogWriteRegex.Matches(content).Cast<System.Text.RegularExpressions.Match>())
            {
                ordinal++;
                var line = RatchetTestRunner.LineNumberAt(content, match.Index);
                yield return $"{rel}:AuditLogEntries-write#{ordinal} # L{line}";
            }
        }
    }
}
