using Humans.Base.Extensions;
using Humans.Base.Interfaces;
using Humans.Gdpr.Contracts;
using Humans.Issues.Data;
using Humans.Issues.Domain;
using NodaTime;

namespace Humans.Issues.Services;

internal sealed class IssueDataLifecycle(
    IIssuesRepository repo,
    IClock clock,
    IFileStorage fileStorage,
    ILogger logger) : IApplicationService
{
    internal const string Issues = "Issues";
    internal const string IssueComments = "IssueComments";

    /// <summary>Six months after a terminal state, in days so the cutoff is a simple <see cref="Duration"/>.</summary>
    private static readonly Duration RetentionPeriod = Duration.FromDays(180);

    internal async Task<int> PurgeExpiredAsync(CancellationToken ct = default)
    {
        var cutoff = clock.GetCurrentInstant() - RetentionPeriod;

        var expired = await repo.GetExpiredTerminalAsync(cutoff, ct);
        if (expired.Count == 0) return 0;

        try
        {
            var deleted = await repo.DeleteByIdsAsync(expired.Select(issue => issue.Id).ToList(), ct);
            logger.LogInformation(
                "PurgeExpiredAsync: deleted {Count} issues older than {Cutoff} (retention {Days}d)",
                deleted, cutoff, RetentionPeriod.Days);
            return deleted;
        }
        finally
        {
            await DeleteScreenshotsAsync(expired);
        }
    }

    internal async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(IReadOnlyList<Guid> resolvedUserIds, CancellationToken ct)
    {
        var userIds = resolvedUserIds.ToHashSet();
        var issues = new List<Issue>();
        foreach (var id in userIds)
            issues.AddRange(await repo.GetForUserExportAsync(id, ct));
        var distinctIssues = issues.DistinctBy(i => i.Id).ToList();
        var shaped = distinctIssues.Where(i => userIds.Contains(i.ReporterUserId)).Select(i => new
        {
            i.Title,
            i.Description,
            i.Category,
            i.Section,
            i.Status,
            i.PageUrl,
            i.UserAgent,
            i.AdditionalContext,
            CreatedAt = i.CreatedAt.ToIso8601(),
            ResolvedAt = i.ResolvedAt.ToIso8601(),
            Comments = i.Comments.OrderBy(c => c.CreatedAt).Select(c => new
            {
                c.Content,
                IsFromUser = c.SenderUserId is { } senderId && userIds.Contains(senderId),
                CreatedAt = c.CreatedAt.ToIso8601()
            })
        }).ToList();

        var comments = distinctIssues
            .Where(i => !userIds.Contains(i.ReporterUserId))
            .SelectMany(i => i.Comments)
            .Where(c => c.SenderUserId is { } senderId && userIds.Contains(senderId))
            .OrderBy(c => c.CreatedAt)
            .Select(c => new
            {
                c.IssueId,
                c.Content,
                CreatedAt = c.CreatedAt.ToIso8601()
            }).ToList();

        return [new UserDataSlice(Issues, shaped), new UserDataSlice(IssueComments, comments)];
    }

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [Issues] = null, // Own issues, comments and screenshots are deleted outright.
            [IssueComments] =
                "Partially retained: comments on other people's issues remain as that issue's content, " +
                "with authorship detached (SenderUserId nulled), per GDPR Art. 17(3)(b)."
        };

    internal static IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    /// <summary>Reported issues and their free text are hard-deleted; triage links elsewhere are detached.</summary>
    internal async Task EraseForUserAsync(Guid userId, CancellationToken ct)
    {
        var ownIssues = await repo.GetForUserExportAsync(userId, ct);
        var screenshotKeys = ownIssues.Where(i => i.ReporterUserId == userId)
            .Select(i => i.ScreenshotStoragePath).OfType<string>().Distinct(StringComparer.Ordinal);
        ct = CancellationToken.None;
        foreach (var key in screenshotKeys)
            await fileStorage.DeleteAsync(key, ct);

        await repo.EraseForUserAsync(userId, ct);
    }

    /// <summary>
    /// Best-effort cleanup of pre-read screenshots whose issue rows are confirmed deleted.
    /// </summary>
    private async Task DeleteScreenshotsAsync(IReadOnlyList<Issue> issues)
    {
        foreach (var issue in issues)
        {
            if (issue.ScreenshotStoragePath is not { } key) continue;
            try
            {
                if (await repo.GetByIdAsync(issue.Id, CancellationToken.None) is not null) continue;
                await fileStorage.DeleteAsync(key, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to verify or delete issue screenshot {Key}", key);
            }
        }
    }
}
