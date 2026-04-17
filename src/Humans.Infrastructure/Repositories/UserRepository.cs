using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Helpers;
using Humans.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.Infrastructure.Repositories;

/// <summary>
/// EF-backed implementation of <see cref="IUserRepository"/>. The only
/// non-test file that touches <c>DbContext.Users</c> and
/// <c>DbContext.EventParticipations</c> after the User migration lands.
/// </summary>
public sealed class UserRepository : IUserRepository
{
    private readonly HumansDbContext _dbContext;

    public UserRepository(HumansDbContext dbContext)
    {
        _dbContext = dbContext;
    }

    // ---- User lookups ----

    public Task<User?> GetByIdAsync(Guid userId, CancellationToken ct = default) =>
        _dbContext.Users
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

    public Task<User?> GetByIdReadOnlyAsync(Guid userId, CancellationToken ct = default) =>
        _dbContext.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

    public async Task<IReadOnlyDictionary<Guid, User>> GetByIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, User>();

        var list = await _dbContext.Users
            .AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToListAsync(ct);

        return list.ToDictionary(u => u.Id);
    }

    public async Task<IReadOnlyList<User>> GetAllAsync(CancellationToken ct = default) =>
        await _dbContext.Users
            .AsNoTracking()
            .ToListAsync(ct);

    public async Task<User?> GetByEmailOrAlternateAsync(string email, CancellationToken ct = default)
    {
        var normalized = EmailNormalization.NormalizeForComparison(email);
        var alternate = GetAlternateEmail(normalized);

        if (alternate is null)
        {
            return await _dbContext.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u =>
                    (u.Email != null && EF.Functions.ILike(u.Email, normalized)) ||
                    (u.GoogleEmail != null && EF.Functions.ILike(u.GoogleEmail, normalized)),
                    ct);
        }

        return await _dbContext.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u =>
                (u.Email != null && (
                    EF.Functions.ILike(u.Email, normalized) ||
                    EF.Functions.ILike(u.Email, alternate))) ||
                (u.GoogleEmail != null && (
                    EF.Functions.ILike(u.GoogleEmail, normalized) ||
                    EF.Functions.ILike(u.GoogleEmail, alternate))),
                ct);
    }

    public async Task<IReadOnlyList<User>> GetContactUsersAsync(string? search, CancellationToken ct = default)
    {
        var query = _dbContext.Users
            .AsNoTracking()
            .Where(u => u.ContactSource != null && u.LastLoginAt == null);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search}%";
            query = query.Where(u =>
                EF.Functions.ILike(u.DisplayName, pattern) ||
                (u.Email != null && EF.Functions.ILike(u.Email, pattern)));
        }

        return await query
            .OrderByDescending(u => u.CreatedAt)
            .ToListAsync(ct);
    }

    // ---- User mutations ----

    public async Task<bool> TrySetGoogleEmailAsync(Guid userId, string email, CancellationToken ct = default)
    {
        var user = await _dbContext.Users.FindAsync([userId], ct);
        if (user is null)
            return false;

        if (user.GoogleEmail is not null)
            return false;

        user.GoogleEmail = email;
        await _dbContext.SaveChangesAsync(ct);
        return true;
    }

    public async Task UpdateDisplayNameAsync(Guid userId, string displayName, CancellationToken ct = default)
    {
        var user = await _dbContext.Users.FindAsync([userId], ct);
        if (user is null)
            return;

        user.DisplayName = displayName;
        await _dbContext.SaveChangesAsync(ct);
    }

    public async Task<bool> SetDeletionPendingAsync(Guid userId, Instant requestedAt, Instant scheduledFor, CancellationToken ct = default)
    {
        var user = await _dbContext.Users.FindAsync([userId], ct);
        if (user is null)
            return false;

        user.DeletionRequestedAt = requestedAt;
        user.DeletionScheduledFor = scheduledFor;
        await _dbContext.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> ClearDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        var user = await _dbContext.Users.FindAsync([userId], ct);
        if (user is null)
            return false;

        user.DeletionRequestedAt = null;
        user.DeletionScheduledFor = null;
        user.DeletionEligibleAfter = null;
        await _dbContext.SaveChangesAsync(ct);
        return true;
    }

    // ---- EventParticipation ----

    public Task<EventParticipation?> GetParticipationAsync(Guid userId, int year, CancellationToken ct = default) =>
        _dbContext.EventParticipations
            .FirstOrDefaultAsync(ep => ep.UserId == userId && ep.Year == year, ct);

    public Task<List<EventParticipation>> GetAllParticipationsForYearAsync(int year, CancellationToken ct = default) =>
        _dbContext.EventParticipations
            .Where(ep => ep.Year == year)
            .ToListAsync(ct);

    public async Task<IReadOnlyDictionary<Guid, EventParticipation>> GetParticipationsForYearByUserIdAsync(
        int year, CancellationToken ct = default)
    {
        var list = await _dbContext.EventParticipations
            .Where(ep => ep.Year == year)
            .ToListAsync(ct);

        return list.ToDictionary(ep => ep.UserId);
    }

    public Task AddParticipationAsync(EventParticipation entry, CancellationToken ct = default)
    {
        _dbContext.EventParticipations.Add(entry);
        return Task.CompletedTask;
    }

    public Task RemoveParticipationAsync(EventParticipation entry, CancellationToken ct = default)
    {
        _dbContext.EventParticipations.Remove(entry);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken ct = default) =>
        _dbContext.SaveChangesAsync(ct);

    // ---- Helpers ----

    private static string? GetAlternateEmail(string normalizedEmail)
    {
        if (normalizedEmail.EndsWith("@gmail.com", StringComparison.Ordinal))
            return $"{normalizedEmail[..^"@gmail.com".Length]}@googlemail.com";

        return null;
    }
}
