using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using MemberApplication = Humans.Domain.Entities.Application;

namespace Humans.Infrastructure.Repositories.Governance;

/// <summary>
/// EF-backed implementation of <see cref="IApplicationRepository"/>. The only
/// non-test file that touches <c>DbContext.Applications</c>,
/// <c>DbContext.BoardVotes</c>, or <c>DbContext.ApplicationStateHistories</c>
/// after the Governance migration lands.
/// </summary>
public sealed class ApplicationRepository : IApplicationRepository
{
    private readonly HumansDbContext _dbContext;

    public ApplicationRepository(HumansDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public Task<MemberApplication?> GetByIdAsync(Guid applicationId, CancellationToken ct = default) =>
        _dbContext.Applications
            .Include(a => a.BoardVotes)
            .Include(a => a.StateHistory)
            .FirstOrDefaultAsync(a => a.Id == applicationId, ct);

    public async Task<IReadOnlyList<MemberApplication>> GetByUserIdAsync(Guid userId, CancellationToken ct = default) =>
        await _dbContext.Applications
            .Include(a => a.StateHistory)
            .Where(a => a.UserId == userId)
            .OrderByDescending(a => a.SubmittedAt)
            .ToListAsync(ct);

    public Task<bool> AnySubmittedForUserAsync(Guid userId, CancellationToken ct = default) =>
        _dbContext.Applications.AnyAsync(
            a => a.UserId == userId && a.Status == ApplicationStatus.Submitted,
            ct);

    public Task<int> CountByStatusAsync(ApplicationStatus status, CancellationToken ct = default) =>
        _dbContext.Applications.CountAsync(a => a.Status == status, ct);

    public async Task<(IReadOnlyList<MemberApplication> Items, int TotalCount)> GetFilteredAsync(
        ApplicationStatus? status,
        MembershipTier? tier,
        int page,
        int pageSize,
        CancellationToken ct = default)
    {
        var query = _dbContext.Applications.AsNoTracking().AsQueryable();

        query = status is null
            ? query.Where(a => a.Status == ApplicationStatus.Submitted)
            : query.Where(a => a.Status == status);

        if (tier is not null)
        {
            query = query.Where(a => a.MembershipTier == tier);
        }

        var totalCount = await query.CountAsync(ct);

        var items = await query
            .OrderBy(a => a.SubmittedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(ct);

        return (items, totalCount);
    }

    public async Task AddAsync(MemberApplication application, CancellationToken ct = default)
    {
        _dbContext.Applications.Add(application);
        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task UpdateAsync(MemberApplication application, CancellationToken ct = default)
    {
        _dbContext.Applications.Update(application);
        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task FinalizeAsync(MemberApplication application, CancellationToken ct = default)
    {
        // Attach-or-update the mutated application. The caller has already
        // fired app.Approve()/app.Reject() which appended a StateHistory row
        // through the aggregate-local collection — EF will cascade-insert it.
        _dbContext.Applications.Update(application);

        // Remove BoardVotes for this application through the change tracker
        // so they commit in the same SaveChangesAsync transaction as the
        // Application update. Load first (the aggregate-local nav may not
        // carry every vote when called directly with a loose entity), then
        // RemoveRange. ExecuteDeleteAsync would be cheaper at scale but is
        // not supported by the EF InMemory provider used in unit tests.
        var votes = await _dbContext.BoardVotes
            .Where(bv => bv.ApplicationId == application.Id)
            .ToListAsync(ct);
        _dbContext.BoardVotes.RemoveRange(votes);

        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetVoterIdsForApplicationAsync(Guid applicationId, CancellationToken ct = default) =>
        await _dbContext.BoardVotes
            .AsNoTracking()
            .Where(bv => bv.ApplicationId == applicationId)
            .Select(bv => bv.BoardMemberUserId)
            .Distinct()
            .ToListAsync(ct);

    public async Task<IReadOnlySet<Guid>> GetUserIdsWithSubmittedAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return new HashSet<Guid>();

        var matched = await _dbContext.Applications
            .AsNoTracking()
            .Where(a => userIds.Contains(a.UserId) && a.Status == ApplicationStatus.Submitted)
            .Select(a => a.UserId)
            .Distinct()
            .ToListAsync(ct);

        return matched.ToHashSet();
    }

    public Task<MemberApplication?> GetSubmittedForUserAsync(
        Guid userId, CancellationToken ct = default) =>
        _dbContext.Applications
            .AsNoTracking()
            .FirstOrDefaultAsync(
                a => a.UserId == userId && a.Status == ApplicationStatus.Submitted,
                ct);

    public async Task<IReadOnlyList<MembershipTier>> GetApprovedTiersForUserAsync(
        Guid userId, CancellationToken ct = default) =>
        await _dbContext.Applications
            .AsNoTracking()
            .Where(a => a.UserId == userId && a.Status == ApplicationStatus.Approved)
            .Select(a => a.MembershipTier)
            .Distinct()
            .ToListAsync(ct);

    public async Task<IReadOnlyList<MemberApplication>> GetAllSubmittedWithVotesAsync(
        CancellationToken ct = default) =>
        await _dbContext.Applications
            .AsNoTracking()
            .Include(a => a.BoardVotes)
            .Where(a => a.Status == ApplicationStatus.Submitted)
            .OrderBy(a => a.MembershipTier)
            .ThenBy(a => a.SubmittedAt)
            .ToListAsync(ct);

    public Task<bool> HasBoardVotesAsync(Guid applicationId, CancellationToken ct = default) =>
        _dbContext.BoardVotes.AnyAsync(v => v.ApplicationId == applicationId, ct);

    public Task<BoardVote?> GetBoardVoteAsync(
        Guid applicationId, Guid boardMemberUserId, CancellationToken ct = default) =>
        _dbContext.BoardVotes
            .AsNoTracking()
            .FirstOrDefaultAsync(
                v => v.ApplicationId == applicationId && v.BoardMemberUserId == boardMemberUserId,
                ct);

    public async Task UpsertBoardVoteAsync(
        Guid applicationId,
        Guid boardMemberUserId,
        VoteChoice vote,
        string? note,
        Instant now,
        CancellationToken ct = default)
    {
        var existing = await _dbContext.BoardVotes
            .FirstOrDefaultAsync(
                v => v.ApplicationId == applicationId && v.BoardMemberUserId == boardMemberUserId,
                ct);

        if (existing is not null)
        {
            existing.Vote = vote;
            existing.Note = note;
            existing.UpdatedAt = now;
        }
        else
        {
            _dbContext.BoardVotes.Add(new BoardVote
            {
                Id = Guid.NewGuid(),
                ApplicationId = applicationId,
                BoardMemberUserId = boardMemberUserId,
                Vote = vote,
                Note = note,
                VotedAt = now
            });
        }

        await _dbContext.SaveChangesAsync(ct);
    }

    public Task<int> GetUnvotedCountForBoardMemberAsync(
        Guid boardMemberUserId, CancellationToken ct = default) =>
        _dbContext.Applications.CountAsync(
            a => a.Status == ApplicationStatus.Submitted &&
                 !a.BoardVotes.Any(v => v.BoardMemberUserId == boardMemberUserId),
            ct);

    public async Task<ApplicationAdminStats> GetAdminStatsAsync(CancellationToken ct = default)
    {
        var stats = await _dbContext.Applications
            .AsNoTracking()
            .Where(a => a.Status != ApplicationStatus.Withdrawn)
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total = g.Count(),
                Approved = g.Count(a => a.Status == ApplicationStatus.Approved),
                Rejected = g.Count(a => a.Status == ApplicationStatus.Rejected),
                Colaborador = g.Count(a => a.MembershipTier == MembershipTier.Colaborador),
                Asociado = g.Count(a => a.MembershipTier == MembershipTier.Asociado)
            })
            .FirstOrDefaultAsync(ct);

        return stats is null
            ? new ApplicationAdminStats(0, 0, 0, 0, 0)
            : new ApplicationAdminStats(
                stats.Total, stats.Approved, stats.Rejected, stats.Colaborador, stats.Asociado);
    }
}
