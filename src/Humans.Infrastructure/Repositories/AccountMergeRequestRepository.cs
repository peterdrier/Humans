using Microsoft.EntityFrameworkCore;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Repositories;

/// <summary>
/// EF-backed implementation of <see cref="IAccountMergeRequestRepository"/>.
/// </summary>
public sealed class AccountMergeRequestRepository : IAccountMergeRequestRepository
{
    private readonly HumansDbContext _dbContext;

    public AccountMergeRequestRepository(HumansDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlySet<Guid>> GetPendingEmailIdsAsync(
        IReadOnlyList<Guid> emailIds, CancellationToken ct = default)
    {
        if (emailIds.Count == 0)
            return new HashSet<Guid>();

        var ids = await _dbContext.AccountMergeRequests
            .Where(r => emailIds.Contains(r.PendingEmailId)
                && r.Status == AccountMergeRequestStatus.Pending)
            .Select(r => r.PendingEmailId)
            .ToListAsync(ct);

        return ids.ToHashSet();
    }

    public async Task<bool> HasPendingForUserAndEmailAsync(
        Guid targetUserId, string normalizedEmail, string? alternateEmail,
        CancellationToken ct = default) =>
        alternateEmail is null
            ? await _dbContext.AccountMergeRequests.AnyAsync(
                r => r.TargetUserId == targetUserId
                    && EF.Functions.ILike(r.Email, normalizedEmail)
                    && r.Status == AccountMergeRequestStatus.Pending, ct)
            : await _dbContext.AccountMergeRequests.AnyAsync(
                r => r.TargetUserId == targetUserId
                    && (EF.Functions.ILike(r.Email, normalizedEmail) ||
                        EF.Functions.ILike(r.Email, alternateEmail))
                    && r.Status == AccountMergeRequestStatus.Pending, ct);

    public async Task<bool> HasPendingForEmailIdAsync(
        Guid pendingEmailId, CancellationToken ct = default) =>
        await _dbContext.AccountMergeRequests
            .AnyAsync(r => r.PendingEmailId == pendingEmailId
                && r.Status == AccountMergeRequestStatus.Pending, ct);

    public async Task AddAsync(AccountMergeRequest request, CancellationToken ct = default)
    {
        _dbContext.AccountMergeRequests.Add(request);
        await _dbContext.SaveChangesAsync(ct);
    }
}
