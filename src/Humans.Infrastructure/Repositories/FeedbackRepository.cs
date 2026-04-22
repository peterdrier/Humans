using Microsoft.EntityFrameworkCore;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Repositories;

/// <summary>
/// EF-backed implementation of <see cref="IFeedbackRepository"/>. The only
/// non-test file that touches <c>DbContext.FeedbackReports</c> or
/// <c>DbContext.FeedbackMessages</c> after the Feedback migration lands.
/// Uses the Scoped + <c>HumansDbContext</c> pattern because Feedback is
/// admin-only and low-traffic (mirrors <see cref="ApplicationRepository"/>).
/// </summary>
public sealed class FeedbackRepository : IFeedbackRepository
{
    private readonly HumansDbContext _dbContext;

    public FeedbackRepository(HumansDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // ==========================================================================
    // Reads
    // ==========================================================================

    public async Task<FeedbackReport?> GetByIdAsync(Guid id, CancellationToken ct = default) =>
        await _dbContext.FeedbackReports
            .AsNoTracking()
            .Include(f => f.Messages.OrderBy(m => m.CreatedAt))
            .FirstOrDefaultAsync(f => f.Id == id, ct);

    public async Task<FeedbackReport?> FindForMutationAsync(Guid id, CancellationToken ct = default) =>
        await _dbContext.FeedbackReports.FindAsync([id], ct);

    public async Task<IReadOnlyList<FeedbackReport>> GetListAsync(
        FeedbackStatus? status,
        FeedbackCategory? category,
        Guid? reporterUserId,
        Guid? assignedToUserId,
        Guid? assignedToTeamId,
        bool? unassignedOnly,
        int limit,
        CancellationToken ct = default)
    {
        var query = _dbContext.FeedbackReports
            .AsNoTracking()
            .Include(f => f.Messages)
            .AsQueryable();

        if (status.HasValue)
            query = query.Where(f => f.Status == status.Value);

        if (category.HasValue)
            query = query.Where(f => f.Category == category.Value);

        if (reporterUserId.HasValue)
            query = query.Where(f => f.UserId == reporterUserId.Value);

        if (assignedToUserId.HasValue)
            query = query.Where(f => f.AssignedToUserId == assignedToUserId.Value);

        if (assignedToTeamId.HasValue)
            query = query.Where(f => f.AssignedToTeamId == assignedToTeamId.Value);

        if (unassignedOnly == true)
            query = query.Where(f => f.AssignedToUserId == null && f.AssignedToTeamId == null);

        return await query
            .OrderByDescending(f => f.CreatedAt)
            .Take(limit)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<FeedbackMessage>> GetMessagesAsync(
        Guid reportId, CancellationToken ct = default) =>
        await _dbContext.FeedbackMessages
            .AsNoTracking()
            .Where(m => m.FeedbackReportId == reportId)
            .OrderBy(m => m.CreatedAt)
            .ToListAsync(ct);

    public Task<int> GetActionableCountAsync(CancellationToken ct = default) =>
        _dbContext.FeedbackReports
            .Where(f => f.Status != FeedbackStatus.Resolved && f.Status != FeedbackStatus.WontFix)
            .CountAsync(f =>
                (f.Status == FeedbackStatus.Open && f.LastAdminMessageAt == null) ||
                (f.LastReporterMessageAt != null &&
                 (f.LastAdminMessageAt == null || f.LastReporterMessageAt > f.LastAdminMessageAt)),
                ct);

    public async Task<IReadOnlyList<(Guid UserId, int Count)>> GetReporterCountsAsync(
        CancellationToken ct = default)
    {
        var rows = await _dbContext.FeedbackReports
            .AsNoTracking()
            .GroupBy(f => f.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToListAsync(ct);

        return rows.Select(r => (r.UserId, r.Count)).ToList();
    }

    public async Task<IReadOnlyList<FeedbackReport>> GetForUserExportAsync(
        Guid userId, CancellationToken ct = default) =>
        await _dbContext.FeedbackReports
            .AsNoTracking()
            .Include(fr => fr.Messages)
            .Where(fr => fr.UserId == userId)
            .OrderByDescending(fr => fr.CreatedAt)
            .ToListAsync(ct);

    // ==========================================================================
    // Writes
    // ==========================================================================

    public async Task AddReportAsync(FeedbackReport report, CancellationToken ct = default)
    {
        _dbContext.FeedbackReports.Add(report);
        await _dbContext.SaveChangesAsync(ct);
    }

    public Task SaveTrackedReportAsync(FeedbackReport report, CancellationToken ct = default) =>
        _dbContext.SaveChangesAsync(ct);

    public async Task AddMessageAndSaveReportAsync(
        FeedbackMessage message, FeedbackReport report, CancellationToken ct = default)
    {
        _dbContext.FeedbackMessages.Add(message);
        // report is assumed tracked (fetched via FindForMutationAsync).
        await _dbContext.SaveChangesAsync(ct);
    }
}
