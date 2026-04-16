using Microsoft.EntityFrameworkCore;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Repositories;

/// <summary>
/// EF-backed implementation of <see cref="IUserEmailRepository"/>. The only
/// non-test file that touches <c>DbContext.UserEmails</c> after the Profile
/// migration lands.
/// </summary>
public sealed class UserEmailRepository : IUserEmailRepository
{
    private readonly HumansDbContext _dbContext;

    public UserEmailRepository(HumansDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    public async Task<IReadOnlyList<UserEmail>> GetByUserIdReadOnlyAsync(
        Guid userId, CancellationToken ct = default) =>
        await _dbContext.UserEmails
            .AsNoTracking()
            .Where(e => e.UserId == userId)
            .OrderBy(e => e.DisplayOrder)
            .ThenBy(e => e.CreatedAt)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<UserEmail>> GetByUserIdTrackedAsync(
        Guid userId, CancellationToken ct = default) =>
        await _dbContext.UserEmails
            .Where(e => e.UserId == userId)
            .ToListAsync(ct);

    public Task<UserEmail?> GetByIdAndUserIdAsync(
        Guid emailId, Guid userId, CancellationToken ct = default) =>
        _dbContext.UserEmails
            .FirstOrDefaultAsync(e => e.Id == emailId && e.UserId == userId, ct);

    public Task<UserEmail?> GetByIdReadOnlyAsync(Guid emailId, CancellationToken ct = default) =>
        _dbContext.UserEmails
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == emailId, ct);

    public Task<UserEmail?> GetPendingVerificationAsync(
        Guid userId, CancellationToken ct = default) =>
        _dbContext.UserEmails
            .FirstOrDefaultAsync(e => e.UserId == userId && !e.IsVerified && !e.IsOAuth, ct);

    public Task<UserEmail?> GetOAuthEmailAsync(
        Guid userId, CancellationToken ct = default) =>
        _dbContext.UserEmails
            .FirstOrDefaultAsync(e => e.UserId == userId && e.IsOAuth, ct);

    public async Task<bool> ExistsForUserAsync(
        Guid userId, string normalizedEmail, string? alternateEmail,
        CancellationToken ct = default) =>
        alternateEmail is null
            ? await _dbContext.UserEmails.AnyAsync(
                e => e.UserId == userId && EF.Functions.ILike(e.Email, normalizedEmail), ct)
            : await _dbContext.UserEmails.AnyAsync(
                e => e.UserId == userId &&
                    (EF.Functions.ILike(e.Email, normalizedEmail) ||
                     EF.Functions.ILike(e.Email, alternateEmail)), ct);

    public async Task<bool> ExistsVerifiedForOtherUserAsync(
        Guid userId, string normalizedEmail, string? alternateEmail,
        CancellationToken ct = default) =>
        alternateEmail is null
            ? await _dbContext.UserEmails.AnyAsync(
                e => e.UserId != userId && e.IsVerified &&
                    EF.Functions.ILike(e.Email, normalizedEmail), ct)
            : await _dbContext.UserEmails.AnyAsync(
                e => e.UserId != userId && e.IsVerified &&
                    (EF.Functions.ILike(e.Email, normalizedEmail) ||
                     EF.Functions.ILike(e.Email, alternateEmail)), ct);

    public Task<UserEmail?> GetConflictingVerifiedEmailAsync(
        Guid excludeEmailId, string normalizedEmail, string? alternateEmail,
        CancellationToken ct = default) =>
        alternateEmail is null
            ? _dbContext.UserEmails.FirstOrDefaultAsync(
                e => e.Id != excludeEmailId && e.IsVerified &&
                    EF.Functions.ILike(e.Email, normalizedEmail), ct)
            : _dbContext.UserEmails.FirstOrDefaultAsync(
                e => e.Id != excludeEmailId && e.IsVerified &&
                    (EF.Functions.ILike(e.Email, normalizedEmail) ||
                     EF.Functions.ILike(e.Email, alternateEmail)), ct);

    public async Task<int> GetMaxDisplayOrderAsync(Guid userId, CancellationToken ct = default) =>
        await _dbContext.UserEmails
            .Where(e => e.UserId == userId)
            .MaxAsync(e => (int?)e.DisplayOrder, ct) ?? -1;

    public async Task<string?> GetNobodiesTeamEmailAsync(
        Guid userId, CancellationToken ct = default) =>
        await _dbContext.UserEmails
            .Where(ue => ue.UserId == userId && ue.IsVerified
                && EF.Functions.ILike(ue.Email, "%@nobodies.team"))
            .Select(ue => ue.Email)
            .FirstOrDefaultAsync(ct);

    public async Task<bool> HasNobodiesTeamEmailAsync(
        Guid userId, CancellationToken ct = default) =>
        await _dbContext.UserEmails
            .AnyAsync(ue => ue.UserId == userId && ue.IsVerified
                && EF.Functions.ILike(ue.Email, "%@nobodies.team"), ct);

    public async Task<Dictionary<Guid, bool>> GetNobodiesTeamEmailStatusByUserAsync(
        CancellationToken ct = default)
    {
        var nobodiesTeamEmails = await _dbContext.UserEmails
            .AsNoTracking()
            .Where(ue => ue.IsVerified && EF.Functions.ILike(ue.Email, "%@nobodies.team"))
            .Select(ue => new { ue.UserId, ue.IsNotificationTarget })
            .ToListAsync(ct);

        return nobodiesTeamEmails
            .GroupBy(e => e.UserId)
            .ToDictionary(
                g => g.Key,
                g => g.Any(e => e.IsNotificationTarget));
    }

    public async Task<Dictionary<Guid, string>> GetNobodiesTeamEmailsByUserIdsAsync(
        IEnumerable<Guid> userIds, CancellationToken ct = default)
    {
        var userIdList = userIds.ToList();
        if (userIdList.Count == 0)
            return new Dictionary<Guid, string>();

        return await _dbContext.UserEmails
            .AsNoTracking()
            .Where(ue => userIdList.Contains(ue.UserId)
                && ue.IsVerified
                && EF.Functions.ILike(ue.Email, "%@nobodies.team"))
            .GroupBy(ue => ue.UserId)
            .Select(g => new
            {
                UserId = g.Key,
                Email = g.OrderByDescending(ue => ue.IsNotificationTarget)
                    .ThenBy(ue => ue.CreatedAt)
                    .Select(ue => ue.Email)
                    .First()
            })
            .ToDictionaryAsync(x => x.UserId, x => x.Email, ct);
    }

    public async Task<Dictionary<Guid, string>> GetAllNotificationTargetEmailsAsync(
        CancellationToken ct = default)
    {
        return await _dbContext.UserEmails
            .AsNoTracking()
            .Where(e => e.IsVerified && e.IsNotificationTarget)
            .GroupBy(e => e.UserId)
            .Select(g => new
            {
                UserId = g.Key,
                Email = g.Select(e => e.Email).First()
            })
            .ToDictionaryAsync(x => x.UserId, x => x.Email, ct);
    }

    public async Task AddAsync(UserEmail email, CancellationToken ct = default)
    {
        _dbContext.UserEmails.Add(email);
        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(UserEmail email, CancellationToken ct = default)
    {
        _dbContext.UserEmails.Remove(email);
        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task RemoveAllForUserAsync(Guid userId, CancellationToken ct = default)
    {
        var emails = await _dbContext.UserEmails
            .Where(e => e.UserId == userId)
            .ToListAsync(ct);

        _dbContext.UserEmails.RemoveRange(emails);
        await _dbContext.SaveChangesAsync(ct);
    }

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        _dbContext.SaveChangesAsync(ct);
}
