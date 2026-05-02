using Humans.Application.Interfaces.Issues;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces.Repositories;

public interface IIssuesRepository
{
    Task AddIssueAsync(Issue issue, CancellationToken ct = default);

    Task<Issue?> GetByIdAsync(Guid id, CancellationToken ct = default);

    /// <summary>Returns issue with comments (.Include) loaded; cross-domain navs are NOT included.</summary>
    Task<Issue?> FindForMutationAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<Issue>> GetListAsync(
        IssueListFilter filter,
        IReadOnlySet<string>? sectionFilter,
        Guid? reporterFallback,
        CancellationToken ct = default);

    Task SaveTrackedIssueAsync(Issue issue, CancellationToken ct = default);

    Task AddCommentAndSaveIssueAsync(IssueComment comment, Issue issue, CancellationToken ct = default);

    /// <summary>For the nav-badge query.</summary>
    Task<int> CountActionableAsync(
        IReadOnlySet<string>? sectionFilter, Guid? viewerFallback,
        CancellationToken ct = default);

    Task<IReadOnlyList<DistinctReporterRow>> GetReporterCountsAsync(CancellationToken ct = default);

    /// <summary>For GDPR export.</summary>
    Task<IReadOnlyList<Issue>> GetForUserExportAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Returns the IDs and screenshot storage paths of issues whose
    /// <c>ResolvedAt</c> is non-null and at or before <paramref name="cutoff"/>.
    /// Used by the retention job to find rows ready for deletion.
    /// </summary>
    Task<IReadOnlyList<ExpiredIssueRow>> GetExpiredTerminalAsync(
        Instant cutoff, CancellationToken ct = default);

    /// <summary>
    /// Deletes the supplied issue rows. Comments cascade via FK. No-op when the
    /// list is empty. Returns the number of rows actually removed.
    /// </summary>
    Task<int> DeleteByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);
}

/// <summary>
/// Projection used by the retention job. <see cref="ScreenshotStoragePath"/> is
/// the relative path stored on the issue (under <c>wwwroot/uploads/issues/{id}/</c>);
/// null when the issue had no screenshot.
/// </summary>
public sealed record ExpiredIssueRow(Guid Id, string? ScreenshotStoragePath);
