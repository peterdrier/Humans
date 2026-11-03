using Humans.Issues.Services;
using Humans.Issues.Contracts;
using Humans.Issues.Domain;
using NodaTime;
using Humans.Base.Interfaces.Repositories;

namespace Humans.Issues.Data;

internal interface IIssuesRepository : IRepository
{
    Task AddIssueAsync(Issue issue, CancellationToken ct = default);

    /// <summary>Detached issue with comments, also used by mutations that attach it when saving.</summary>
    Task<Issue?> GetByIdAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<Issue>> GetListAsync(
        IssueListFilter filter,
        IReadOnlySet<string>? sectionFilter,
        IReadOnlyCollection<Guid>? reporterFallback,
        IReadOnlyCollection<Guid>? reporterFilter,
        IReadOnlyCollection<Guid>? assigneeFilter,
        CancellationToken ct = default);

    Task SaveTrackedIssueAsync(Issue issue, CancellationToken ct = default);

    Task AddCommentAndSaveIssueAsync(IssueComment comment, Issue issue, CancellationToken ct = default);

    Task<int> CountActionableAsync(
        IReadOnlySet<string>? sectionFilter, IReadOnlyCollection<Guid>? viewerFallback,
        CancellationToken ct = default);

    Task<IReadOnlyList<DistinctReporterRow>> GetReporterCountsAsync(CancellationToken ct = default);

    /// <summary>
    /// Issues reported by the user or carrying their comments. The export service limits other people's issues to
    /// the user's own comments.
    /// </summary>
    Task<IReadOnlyList<Issue>> GetForUserExportAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Ids of issues whose <c>ResolvedAt</c> is at or before <paramref name="cutoff"/>.</summary>
    Task<IReadOnlyList<Guid>> GetExpiredTerminalAsync(
        Instant cutoff, CancellationToken ct = default);

    /// <summary>Comments cascade. Returns rows removed and their screenshot keys, for the caller to delete.</summary>
    Task<(int Deleted, IReadOnlyList<string> ScreenshotKeys)> DeleteByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default);

    /// <summary>
    /// GDPR Art. 17: hard-deletes the user's reported issues and detaches them from comments and triage fields on
    /// other people's.
    /// </summary>
    Task EraseForUserAsync(Guid userId, CancellationToken ct = default);
}

