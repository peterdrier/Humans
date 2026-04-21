using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Repositories;

/// <summary>
/// EF-backed implementation of <see cref="IUserRepository"/>. The only
/// non-test file that touches <c>DbContext.Users</c> or
/// <c>DbContext.EventParticipations</c> after the User migration lands.
/// Uses <see cref="IDbContextFactory{TContext}"/> so the repository can be
/// registered as Singleton while <c>HumansDbContext</c> remains Scoped.
/// </summary>
public sealed class UserRepository : IUserRepository
{
    private readonly IDbContextFactory<HumansDbContext> _factory;

    public UserRepository(IDbContextFactory<HumansDbContext> factory)
    {
        _factory = factory;
    }

    // ==========================================================================
    // Reads — User
    // ==========================================================================

    public async Task<User?> GetByIdAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, ct);
    }

    public async Task<IReadOnlyDictionary<Guid, User>> GetByIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, User>();

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var list = await ctx.Users
            .AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .ToListAsync(ct);

        return list.ToDictionary(u => u.Id);
    }

    public async Task<IReadOnlyList<User>> GetAllAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Users
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetAllUserIdsAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Users
            .AsNoTracking()
            .Select(u => u.Id)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<(string Language, int Count)>>
        GetLanguageDistributionForUserIdsAsync(
            IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return [];

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var grouped = await ctx.Users
            .AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .GroupBy(u => u.PreferredLanguage)
            .Select(g => new { Language = g.Key, Count = g.Count() })
            .OrderByDescending(g => g.Count)
            .ToListAsync(ct);

        return grouped.Select(g => (g.Language, g.Count)).ToList();
    }

    public async Task<User?> GetByEmailOrAlternateAsync(
        string normalizedEmail, string? alternateEmail, CancellationToken ct = default)
    {
        // ILIKE without escape treats '_' and '%' in the input as wildcards,
        // so alex_smith@example.com would also match alexXsmith@example.com.
        // Escape the pattern and pass '\' as the explicit escape character for
        // literal (case-insensitive) matching.
        var escapedEmail = EscapeLikePattern(normalizedEmail);
        var escapedAlternate = alternateEmail is null ? null : EscapeLikePattern(alternateEmail);

        await using var ctx = await _factory.CreateDbContextAsync(ct);

        if (escapedAlternate is null)
        {
            return await ctx.Users
                .AsNoTracking()
                .FirstOrDefaultAsync(u =>
                    (u.Email != null && EF.Functions.ILike(u.Email, escapedEmail, "\\")) ||
                    (u.GoogleEmail != null && EF.Functions.ILike(u.GoogleEmail, escapedEmail, "\\")),
                    ct);
        }

        return await ctx.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u =>
                (u.Email != null && (
                    EF.Functions.ILike(u.Email, escapedEmail, "\\") ||
                    EF.Functions.ILike(u.Email, escapedAlternate, "\\"))) ||
                (u.GoogleEmail != null && (
                    EF.Functions.ILike(u.GoogleEmail, escapedEmail, "\\") ||
                    EF.Functions.ILike(u.GoogleEmail, escapedAlternate, "\\"))),
                ct);
    }

    private static string EscapeLikePattern(string value)
        => value
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");

    public async Task<User?> GetByNormalizedEmailAsync(
        string? normalizedEmail, CancellationToken ct = default)
    {
        if (normalizedEmail is null)
            return null;

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail, ct);
    }

    public async Task<IReadOnlyList<User>> GetContactUsersAsync(
        string? search, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var query = ctx.Users
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

    public async Task<IReadOnlyList<Instant>> GetLoginTimestampsInWindowAsync(
        Instant fromInclusive, Instant toExclusive, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Users
            .AsNoTracking()
            .Where(u => u.LastLoginAt != null
                        && u.LastLoginAt >= fromInclusive
                        && u.LastLoginAt < toExclusive)
            .Select(u => u.LastLoginAt!.Value)
            .ToListAsync(ct);
    }

    public async Task<Guid?> GetOtherUserIdHavingGoogleEmailAsync(
        string email, Guid excludeUserId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Users
            .AsNoTracking()
            .Where(u => u.GoogleEmail != null
                        && EF.Functions.ILike(u.GoogleEmail, email)
                        && u.Id != excludeUserId)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(ct);
    }

    // ==========================================================================
    // Writes — User (atomic field updates)
    // ==========================================================================

    public async Task<bool> UpdateDisplayNameAsync(
        Guid userId, string displayName, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([userId], ct);
        if (user is null)
            return false;

        user.DisplayName = displayName;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> TrySetGoogleEmailAsync(
        Guid userId, string email, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([userId], ct);
        if (user is null || user.GoogleEmail is not null)
            return false;

        user.GoogleEmail = email;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetGoogleEmailAsync(
        Guid userId, string email, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([userId], ct);
        if (user is null)
            return false;

        user.GoogleEmail = email;
        user.GoogleEmailStatus = GoogleEmailStatus.Unknown;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<(bool Updated, string? OldEmail)> RewritePrimaryEmailAsync(
        Guid userId, string newEmail, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([userId], ct);
        if (user is null)
            return (false, null);

        var oldEmail = user.Email;
        user.Email = newEmail;
        user.UserName = newEmail;
        user.NormalizedEmail = newEmail.ToUpperInvariant();
        user.NormalizedUserName = newEmail.ToUpperInvariant();
        await ctx.SaveChangesAsync(ct);
        return (true, oldEmail);
    }

    public async Task<bool> SetDeletionPendingAsync(
        Guid userId, Instant requestedAt, Instant scheduledFor, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([userId], ct);
        if (user is null)
            return false;

        user.DeletionRequestedAt = requestedAt;
        user.DeletionScheduledFor = scheduledFor;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> ClearDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([userId], ct);
        if (user is null)
            return false;

        user.DeletionRequestedAt = null;
        user.DeletionScheduledFor = null;
        user.DeletionEligibleAfter = null;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> AnonymizeForMergeAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([userId], ct);
        if (user is null)
            return false;

        var anonymizedId = $"merged-{user.Id:N}";

        user.DisplayName = "Merged User";
        user.Email = $"{anonymizedId}@merged.local";
        user.NormalizedEmail = user.Email.ToUpperInvariant();
        user.UserName = anonymizedId;
        user.NormalizedUserName = anonymizedId.ToUpperInvariant();
        user.ProfilePictureUrl = null;
        user.PhoneNumber = null;
        user.PhoneNumberConfirmed = false;

        // Clear any deletion request fields — this account is being archived
        // through the merge flow, not the deletion flow.
        user.DeletionRequestedAt = null;
        user.DeletionScheduledFor = null;
        user.DeletionEligibleAfter = null;

        // Disable login
        user.LockoutEnabled = true;
        user.LockoutEnd = DateTimeOffset.MaxValue;
        user.SecurityStamp = Guid.NewGuid().ToString();

        // Clear iCal token so any saved calendar subscription links stop working
        user.ICalToken = null;

        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetContactSourceIfNullAsync(
        Guid userId, ContactSource source, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([userId], ct);
        if (user is null || user.ContactSource is not null)
            return false;

        user.ContactSource = source;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task RemoveExternalLoginsAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var logins = await ctx.Set<IdentityUserLogin<Guid>>()
            .Where(l => l.UserId == userId)
            .ToListAsync(ct);

        if (logins.Count == 0)
            return;

        ctx.Set<IdentityUserLogin<Guid>>().RemoveRange(logins);
        await ctx.SaveChangesAsync(ct);
    }

    public async Task MigrateExternalLoginsAsync(
        Guid sourceUserId, Guid targetUserId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var sourceLogins = await ctx.Set<IdentityUserLogin<Guid>>()
            .Where(l => l.UserId == sourceUserId)
            .ToListAsync(ct);

        if (sourceLogins.Count == 0)
            return;

        var targetProviders = await ctx.Set<IdentityUserLogin<Guid>>()
            .Where(l => l.UserId == targetUserId)
            .Select(l => l.LoginProvider)
            .ToListAsync(ct);
        var targetProviderSet = targetProviders.ToHashSet(StringComparer.Ordinal);

        foreach (var login in sourceLogins)
        {
            ctx.Set<IdentityUserLogin<Guid>>().Remove(login);

            if (!targetProviderSet.Contains(login.LoginProvider))
            {
                ctx.Set<IdentityUserLogin<Guid>>().Add(new IdentityUserLogin<Guid>
                {
                    LoginProvider = login.LoginProvider,
                    ProviderKey = login.ProviderKey,
                    ProviderDisplayName = login.ProviderDisplayName,
                    UserId = targetUserId
                });
            }
        }

        await ctx.SaveChangesAsync(ct);
    }

    public async Task<string?> PurgeAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([userId], ct);
        if (user is null)
            return null;

        var displayName = user.DisplayName;

        // Remove UserEmails so the unique index doesn't block the new account
        var userEmails = await ctx.UserEmails.Where(e => e.UserId == userId).ToListAsync(ct);
        ctx.UserEmails.RemoveRange(userEmails);

        // Change email so email-based lookup won't match
        var purgedEmail = $"purged-{Guid.NewGuid()}@deleted.local";
        user.Email = purgedEmail;
        user.NormalizedEmail = purgedEmail.ToUpperInvariant();
        user.UserName = purgedEmail;
        user.NormalizedUserName = purgedEmail.ToUpperInvariant();
        user.DisplayName = $"Purged ({displayName})";

        // Lock out the account permanently
        user.LockoutEnabled = true;
        user.LockoutEnd = DateTimeOffset.MaxValue;

        await ctx.SaveChangesAsync(ct);
        return displayName;
    }

    public async Task<int> GetPendingDeletionCountAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Users.CountAsync(u => u.DeletionRequestedAt != null, ct);
    }

    // ==========================================================================
    // Reads — EventParticipation
    // ==========================================================================

    public async Task<EventParticipation?> GetParticipationAsync(
        Guid userId, int year, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.EventParticipations
            .AsNoTracking()
            .FirstOrDefaultAsync(ep => ep.UserId == userId && ep.Year == year, ct);
    }

    public async Task<IReadOnlyList<EventParticipation>> GetAllParticipationsForYearAsync(
        int year, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.EventParticipations
            .AsNoTracking()
            .Where(ep => ep.Year == year)
            .ToListAsync(ct);
    }

    // ==========================================================================
    // Writes — EventParticipation
    // ==========================================================================

    public async Task<EventParticipation?> UpsertParticipationAsync(
        Guid userId,
        int year,
        ParticipationStatus status,
        ParticipationSource source,
        Instant? declaredAt,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var existing = await ctx.EventParticipations
            .FirstOrDefaultAsync(ep => ep.UserId == userId && ep.Year == year, ct);

        EventParticipation persisted;
        if (existing is not null)
        {
            // Attended is permanent — never revert.
            if (existing.Status == ParticipationStatus.Attended)
                return null;

            existing.Status = status;
            existing.Source = source;
            existing.DeclaredAt = declaredAt;
            persisted = existing;
        }
        else
        {
            persisted = new EventParticipation
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Year = year,
                Status = status,
                Source = source,
                DeclaredAt = declaredAt,
            };
            ctx.EventParticipations.Add(persisted);
        }

        await ctx.SaveChangesAsync(ct);

        // Detach so callers cannot accidentally mutate a tracked entity through
        // a disposed context.
        ctx.Entry(persisted).State = EntityState.Detached;
        return persisted;
    }

    public async Task<bool> RemoveParticipationAsync(
        Guid userId,
        int year,
        ParticipationSource requiredSource,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var existing = await ctx.EventParticipations
            .FirstOrDefaultAsync(ep => ep.UserId == userId && ep.Year == year, ct);

        if (existing is null ||
            existing.Source != requiredSource ||
            existing.Status == ParticipationStatus.Attended)
        {
            return false;
        }

        ctx.EventParticipations.Remove(existing);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> BackfillParticipationsAsync(
        int year,
        IReadOnlyList<(Guid UserId, ParticipationStatus Status)> entries,
        CancellationToken ct = default)
    {
        if (entries.Count == 0)
            return 0;

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var existing = await ctx.EventParticipations
            .Where(ep => ep.Year == year)
            .ToDictionaryAsync(ep => ep.UserId, ct);

        var count = 0;
        foreach (var (userId, status) in entries)
        {
            if (existing.TryGetValue(userId, out var ep))
            {
                // Attended is permanent — leave it alone.
                if (ep.Status == ParticipationStatus.Attended)
                {
                    count++;
                    continue;
                }

                ep.Status = status;
                ep.Source = ParticipationSource.AdminBackfill;
                ep.DeclaredAt = null;
            }
            else
            {
                var newEp = new EventParticipation
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    Year = year,
                    Status = status,
                    Source = ParticipationSource.AdminBackfill,
                };
                ctx.EventParticipations.Add(newEp);
                existing[userId] = newEp;
            }

            count++;
        }

        await ctx.SaveChangesAsync(ct);
        return count;
    }
}
