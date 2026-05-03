using Humans.Application.Interfaces.Issues;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Infrastructure.Repositories.Issues;

/// <summary>
/// EF-backed implementation of <see cref="IIssuesRepository"/>. The only
/// non-test file that touches <c>DbContext.Issues</c> or
/// <c>DbContext.IssueComments</c>. Uses <see cref="IDbContextFactory{TContext}"/>
/// so the repository can be registered as Singleton while
/// <c>HumansDbContext</c> remains Scoped.
/// </summary>
public sealed class IssuesRepository : IIssuesRepository
{
    private readonly IDbContextFactory<HumansDbContext> _factory;

    public IssuesRepository(IDbContextFactory<HumansDbContext> factory) => _factory = factory;

    public async Task AddIssueAsync(Issue issue, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.Issues.Add(issue);
        await db.SaveChangesAsync(ct);
    }

    public async Task<Issue?> GetByIdAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Issues
            .AsNoTracking()
            .Include(i => i.Comments.OrderBy(c => c.CreatedAt))
            .FirstOrDefaultAsync(i => i.Id == id, ct);
    }

    public async Task<Issue?> FindForMutationAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Issues
            .Include(i => i.Comments)
            .FirstOrDefaultAsync(i => i.Id == id, ct);
    }

    public async Task<IReadOnlyList<Issue>> GetListAsync(
        IssueListFilter f,
        IReadOnlySet<string>? sectionFilter,
        Guid? reporterFallback,
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        IQueryable<Issue> q = db.Issues.AsNoTracking().Include(i => i.Comments);

        if (f.Statuses is { Length: > 0 }) q = q.Where(i => f.Statuses.Contains(i.Status));
        if (f.Categories is { Length: > 0 }) q = q.Where(i => f.Categories.Contains(i.Category));
        if (f.Sections is { Length: > 0 }) q = q.Where(i => f.Sections.Contains(i.Section));
        if (f.ReporterUserId is { } rid) q = q.Where(i => i.ReporterUserId == rid);
        if (f.AssigneeUserId is { } aid) q = q.Where(i => i.AssigneeUserId == aid);
        if (!string.IsNullOrWhiteSpace(f.SearchText))
            q = q.Where(i => i.Title.Contains(f.SearchText) || i.Description.Contains(f.SearchText));

        // Visibility filter:
        //  - sectionFilter null = no constraint (Admin)
        //  - sectionFilter non-null = "section IN sectionFilter OR ReporterUserId == reporterFallback"
        if (sectionFilter is not null)
        {
            var sectionList = sectionFilter.ToList();
            var fallback = reporterFallback;
            q = q.Where(i =>
                (i.Section != null && sectionList.Contains(i.Section)) ||
                (fallback.HasValue && i.ReporterUserId == fallback.Value));
        }

        return await q.OrderByDescending(i => i.UpdatedAt).Take(f.Limit).ToListAsync(ct);
    }

    public async Task SaveTrackedIssueAsync(Issue issue, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        db.Attach(issue);
        db.Entry(issue).State = EntityState.Modified;
        await db.SaveChangesAsync(ct);
    }

    public async Task AddCommentAndSaveIssueAsync(IssueComment comment, Issue issue, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        // Attach the issue (mutated by the caller) and mark it modified so
        // timestamp/status fields are persisted in the same transaction as the
        // new comment.
        db.Attach(issue);
        db.Entry(issue).State = EntityState.Modified;
        db.IssueComments.Add(comment);
        await db.SaveChangesAsync(ct);
    }

    public async Task<int> CountActionableAsync(
        IReadOnlySet<string>? sectionFilter,
        Guid? viewerFallback,
        CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        IQueryable<Issue> q = db.Issues.AsNoTracking()
            .Where(i => i.Status == IssueStatus.Open || i.Status == IssueStatus.Triage);

        if (sectionFilter is not null)
        {
            var sectionList = sectionFilter.ToList();
            var fallback = viewerFallback;
            q = q.Where(i =>
                (i.Section != null && sectionList.Contains(i.Section)) ||
                (fallback.HasValue && i.ReporterUserId == fallback.Value));
        }

        return await q.CountAsync(ct);
    }

    public async Task<IReadOnlyList<DistinctReporterRow>> GetReporterCountsAsync(CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        var rows = await db.Issues.AsNoTracking()
            .GroupBy(i => i.ReporterUserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        // DisplayName left blank — service stitches via IUserService.
        return rows.Select(r => new DistinctReporterRow(r.UserId, string.Empty, r.Count)).ToList();
    }

    public async Task<IReadOnlyList<Issue>> GetForUserExportAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Issues
            .AsNoTracking()
            .Include(i => i.Comments.OrderBy(c => c.CreatedAt))
            .Where(i => i.ReporterUserId == userId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ExpiredIssueRow>> GetExpiredTerminalAsync(
        Instant cutoff, CancellationToken ct = default)
    {
        await using var db = await _factory.CreateDbContextAsync(ct);
        return await db.Issues
            .AsNoTracking()
            .Where(i => i.ResolvedAt != null && i.ResolvedAt <= cutoff)
            .Select(i => new ExpiredIssueRow(i.Id, i.ScreenshotStoragePath))
            .ToListAsync(ct);
    }

    public async Task<int> DeleteByIdsAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct = default)
    {
        if (ids.Count == 0) return 0;

        // Load-then-RemoveRange (matching EmailOutboxRepository.DeleteSentOlderThanAsync)
        // so unit tests using the EF InMemory provider still cover the path.
        // ExecuteDeleteAsync would be cheaper at scale but is not supported by
        // the InMemory provider.
        await using var db = await _factory.CreateDbContextAsync(ct);
        var toDelete = await db.Issues
            .Where(i => ids.Contains(i.Id))
            .ToListAsync(ct);

        if (toDelete.Count == 0) return 0;

        db.Issues.RemoveRange(toDelete);
        await db.SaveChangesAsync(ct);
        return toDelete.Count;
    }
}
