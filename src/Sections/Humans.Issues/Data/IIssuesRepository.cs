using Humans.Issues.Services;
using Humans.Issues.Contracts;
using Humans.Issues.Domain;
using NodaTime;
using Humans.Base.Interfaces.Repositories;

namespace Humans.Issues.Data;

internal interface IIssuesRepository : IRepository
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

    /// <summary>
    /// Returns issues reported by the user or containing their comments. The export service
    /// limits other people's issues to the user's own comments.
    /// </summary>
    Task<IReadOnlyList<Issue>> GetForUserExportAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Ids of issues whose <c>ResolvedAt</c> is at or before <paramref name="cutoff"/>.</summary>
    Task<IReadOnlyList<Guid>> GetExpiredTerminalAsync(
        Instant cutoff, CancellationToken ct = default);

    /// <summary>
    /// Deletes the supplied issue rows. Comments cascade via FK. No-op when the
    /// list is empty. Returns the number of rows actually removed and the storage keys of their
    /// screenshots so the caller can drop the files.
    /// </summary>
    Task<(int Deleted, IReadOnlyList<string> ScreenshotKeys)> DeleteByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    /// <summary>
    /// GDPR Art. 17: hard-deletes the issues the user reported (comments cascade)
    /// and detaches them from comments and triage fields on other people's issues.
    /// Returns the storage keys of the deleted issues' screenshots so the caller can drop the files.
    /// </summary>
    Task<IReadOnlyList<string>> EraseForUserAsync(Guid userId, CancellationToken ct = default);
}

