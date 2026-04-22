using Microsoft.EntityFrameworkCore;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Repositories;

/// <summary>
/// EF-backed implementation of <see cref="IUserEmailRepository"/>. The only
/// non-test file that touches <c>DbContext.UserEmails</c> after the Profile
/// migration lands.
/// Uses <see cref="IDbContextFactory{TContext}"/> so the repository can be
/// registered as Singleton while <c>HumansDbContext</c> remains Scoped.
/// </summary>
public sealed class UserEmailRepository : IUserEmailRepository
{
    private readonly IDbContextFactory<HumansDbContext> _factory;

    public UserEmailRepository(IDbContextFactory<HumansDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyList<UserEmail>> GetByUserIdReadOnlyAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.UserEmails
            .AsNoTracking()
            .Where(e => e.UserId == userId)
            .OrderBy(e => e.DisplayOrder)
            .ThenBy(e => e.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<UserEmail>> GetByUserIdForMutationAsync(
        Guid userId, CancellationToken ct = default)
    {
        // With IDbContextFactory the context is short-lived, so returned entities
        // are detached. Callers must pass mutated entities explicitly back to
        // UpdateAsync / UpdateBatchAsync.
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.UserEmails
            .AsNoTracking()
            .Where(e => e.UserId == userId)
            .ToListAsync(ct);
    }

    public async Task<UserEmail?> GetByIdAndUserIdAsync(
        Guid emailId, Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.UserEmails
            .FirstOrDefaultAsync(e => e.Id == emailId && e.UserId == userId, ct);
    }

    public async Task<UserEmail?> GetByIdReadOnlyAsync(Guid emailId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.UserEmails
            .AsNoTracking()
            .FirstOrDefaultAsync(e => e.Id == emailId, ct);
    }

    public async Task<bool> ExistsForUserAsync(
        Guid userId, string normalizedEmail, string? alternateEmail,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return alternateEmail is null
            ? await ctx.UserEmails.AnyAsync(
                e => e.UserId == userId && EF.Functions.ILike(e.Email, normalizedEmail), ct)
            : await ctx.UserEmails.AnyAsync(
                e => e.UserId == userId &&
                    (EF.Functions.ILike(e.Email, normalizedEmail) ||
                     EF.Functions.ILike(e.Email, alternateEmail)), ct);
    }

    public async Task<bool> ExistsVerifiedForOtherUserAsync(
        Guid userId, string normalizedEmail, string? alternateEmail,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return alternateEmail is null
            ? await ctx.UserEmails.AnyAsync(
                e => e.UserId != userId && e.IsVerified &&
                    EF.Functions.ILike(e.Email, normalizedEmail), ct)
            : await ctx.UserEmails.AnyAsync(
                e => e.UserId != userId && e.IsVerified &&
                    (EF.Functions.ILike(e.Email, normalizedEmail) ||
                     EF.Functions.ILike(e.Email, alternateEmail)), ct);
    }

    public async Task<UserEmail?> GetConflictingVerifiedEmailAsync(
        Guid excludeEmailId, string normalizedEmail, string? alternateEmail,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return alternateEmail is null
            ? await ctx.UserEmails.FirstOrDefaultAsync(
                e => e.Id != excludeEmailId && e.IsVerified &&
                    EF.Functions.ILike(e.Email, normalizedEmail), ct)
            : await ctx.UserEmails.FirstOrDefaultAsync(
                e => e.Id != excludeEmailId && e.IsVerified &&
                    (EF.Functions.ILike(e.Email, normalizedEmail) ||
                     EF.Functions.ILike(e.Email, alternateEmail)), ct);
    }

    public async Task<int> GetMaxDisplayOrderAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.UserEmails
            .Where(e => e.UserId == userId)
            .MaxAsync(e => (int?)e.DisplayOrder, ct) ?? -1;
    }

    public async Task<IReadOnlyList<UserEmail>> GetAllVerifiedNobodiesTeamEmailsAsync(
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.UserEmails
            .AsNoTracking()
            .Where(ue => ue.IsVerified && EF.Functions.ILike(ue.Email, "%@nobodies.team"))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<UserEmail>> GetAllAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.UserEmails
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task RemoveAllForUserAndSaveAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var emails = await ctx.UserEmails
            .Where(e => e.UserId == userId)
            .ToListAsync(ct);

        if (emails.Count == 0)
            return;

        ctx.UserEmails.RemoveRange(emails);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<bool> MarkVerifiedAsync(
        Guid emailId, NodaTime.Instant now, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var email = await ctx.UserEmails.FirstOrDefaultAsync(e => e.Id == emailId, ct);
        if (email is null)
            return false;

        email.IsVerified = true;
        email.UpdatedAt = now;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RemoveByIdAsync(Guid emailId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var email = await ctx.UserEmails.FirstOrDefaultAsync(e => e.Id == emailId, ct);
        if (email is null)
            return false;

        ctx.UserEmails.Remove(email);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<Dictionary<Guid, string>> GetAllNotificationTargetEmailsAsync(
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.UserEmails
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

    public async Task<string?> GetVerifiedEmailAddressAsync(
        Guid userId, Guid emailId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.UserEmails
            .AsNoTracking()
            .Where(ue => ue.Id == emailId && ue.UserId == userId && ue.IsVerified)
            .Select(ue => ue.Email)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<Guid?> GetUserIdByVerifiedEmailAsync(
        string email, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.UserEmails
            .AsNoTracking()
            .Where(ue => ue.Email == email && ue.IsVerified)
            .Select(ue => (Guid?)ue.UserId)
            .FirstOrDefaultAsync(ct);
    }

    public async Task AddAsync(UserEmail email, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.UserEmails.Add(email);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(UserEmail email, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.Attach(email);
        ctx.UserEmails.Remove(email);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task RemoveAllForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var emails = await ctx.UserEmails
            .Where(e => e.UserId == userId)
            .ToListAsync(ct);

        ctx.UserEmails.RemoveRange(emails);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<UserEmailWithUser?> FindVerifiedWithUserAsync(
        string normalizedEmail, string? alternateEmail,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var query = ctx.UserEmails
            .AsNoTracking()
            .Where(ue => ue.IsVerified);

        UserEmail? match;
        if (alternateEmail is null)
        {
            match = await query.FirstOrDefaultAsync(
                ue => EF.Functions.ILike(ue.Email, normalizedEmail), ct);
        }
        else
        {
            match = await query.FirstOrDefaultAsync(
                ue => EF.Functions.ILike(ue.Email, normalizedEmail) ||
                      EF.Functions.ILike(ue.Email, alternateEmail), ct);
        }

        if (match is null)
            return null;

        var user = await ctx.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == match.UserId, ct);

        if (user is null)
            return null;

        return new UserEmailWithUser(
            user.Id,
            match.Email,
            user.ContactSource,
            user.LastLoginAt);
    }

    public async Task UpdateAsync(UserEmail email, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        ctx.Attach(email);
        ctx.Entry(email).State = EntityState.Modified;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task UpdateBatchAsync(IReadOnlyList<UserEmail> emails, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        foreach (var email in emails)
        {
            ctx.Attach(email);
            ctx.Entry(email).State = EntityState.Modified;
        }
        await ctx.SaveChangesAsync(ct);
    }
}
