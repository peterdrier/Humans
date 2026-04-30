using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Repositories.Users;

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

    public async Task<IReadOnlyDictionary<Guid, User>> GetByIdsWithEmailsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, User>();

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var list = await ctx.Users
            .AsNoTracking()
            .Include(u => u.UserEmails)
            .Where(u => userIds.Contains(u.Id))
            .ToListAsync(ct);

        return list.ToDictionary(u => u.Id);
    }

    public async Task<IReadOnlyList<User>> GetAllAsync(CancellationToken ct = default)
    {
        // Include UserEmails so callers reading User.Email get the override's
        // computed value rather than null. Cheap at ~500-user scale.
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Users
            .AsNoTracking()
            .Include(u => u.UserEmails)
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
        // Verified email lookups route through user_emails; the legacy
        // GoogleEmail shadow column survives on disk and is matched as a
        // fallback via EF.Property until the column is dropped. ILIKE without
        // escape treats '_' / '%' in the input as wildcards, so
        // alex_smith@example.com would also match alexXsmith@example.com —
        // escape the pattern with '\'.
        var escapedEmail = EscapeLikePattern(normalizedEmail);
        var escapedAlternate = alternateEmail is null ? null : EscapeLikePattern(alternateEmail);

        await using var ctx = await _factory.CreateDbContextAsync(ct);

        // Step 1: look up by verified UserEmail (canonical email source).
        var userIdByEmail = escapedAlternate is null
            ? await ctx.UserEmails
                .Where(e => e.IsVerified && EF.Functions.ILike(e.Email, escapedEmail, "\\"))
                .Select(e => (Guid?)e.UserId)
                .FirstOrDefaultAsync(ct)
            : await ctx.UserEmails
                .Where(e => e.IsVerified && (
                    EF.Functions.ILike(e.Email, escapedEmail, "\\") ||
                    EF.Functions.ILike(e.Email, escapedAlternate, "\\")))
                .Select(e => (Guid?)e.UserId)
                .FirstOrDefaultAsync(ct);

        if (userIdByEmail is not null)
        {
            return await ctx.Users
                .AsNoTracking()
                .Include(u => u.UserEmails)
                .FirstOrDefaultAsync(u => u.Id == userIdByEmail.Value, ct);
        }

        // Step 2: fall back to the legacy GoogleEmail shadow column.
        if (escapedAlternate is null)
        {
            return await ctx.Users
                .AsNoTracking()
                .Include(u => u.UserEmails)
                .FirstOrDefaultAsync(u =>
                    EF.Property<string?>(u, "GoogleEmail") != null
                    && EF.Functions.ILike(EF.Property<string?>(u, "GoogleEmail")!, escapedEmail, "\\"),
                    ct);
        }

        return await ctx.Users
            .AsNoTracking()
            .Include(u => u.UserEmails)
            .FirstOrDefaultAsync(u =>
                EF.Property<string?>(u, "GoogleEmail") != null && (
                    EF.Functions.ILike(EF.Property<string?>(u, "GoogleEmail")!, escapedEmail, "\\") ||
                    EF.Functions.ILike(EF.Property<string?>(u, "GoogleEmail")!, escapedAlternate, "\\")),
                ct);
    }

    private static string EscapeLikePattern(string value)
        => value
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");

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
            .Where(u => EF.Property<string?>(u, "GoogleEmail") != null
                        && EF.Functions.ILike(EF.Property<string?>(u, "GoogleEmail")!, email)
                        && u.Id != excludeUserId)
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetLegacyGoogleEmailsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, string>();

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var rows = await ctx.Users
            .AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, GoogleEmail = EF.Property<string?>(u, "GoogleEmail") })
            .Where(x => x.GoogleEmail != null)
            .ToListAsync(ct);

        return rows.ToDictionary(x => x.Id, x => x.GoogleEmail!);
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
        if (user is null)
            return false;

        var entry = ctx.Entry(user).Property<string?>("GoogleEmail");
        if (entry.CurrentValue is not null)
            return false;

        entry.CurrentValue = email;
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

        ctx.Entry(user).Property<string?>("GoogleEmail").CurrentValue = email;
        user.GoogleEmailStatus = GoogleEmailStatus.Unknown;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetGoogleEmailStatusAsync(
        Guid userId, GoogleEmailStatus status, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([userId], ct);
        if (user is null)
            return false;

        if (user.GoogleEmailStatus == status)
            return false;

        user.GoogleEmailStatus = status;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<(bool Updated, string? OldEmail)> RewritePrimaryEmailAsync(
        Guid userId, string newEmail, CancellationToken ct = default)
    {
        // The email rename is applied to the UserEmail row by the admin flow's
        // call to IUserEmailService.RewriteEmailAddressAsync; this method only
        // returns the previous value for audit logging. With Identity-column
        // writes removed (PR 2), base.Email is null for users created post-PR 1
        // — pull the old email from UserEmails so the audit log records a real
        // address. Falls back to base.Email for legacy pre-PR 1 users (column
        // still populated) so the audit remains accurate during the transition.
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var user = await ctx.Users
            .AsNoTracking()
            .Include(u => u.UserEmails)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null)
            return (false, null);

        return (true, user.Email);
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

        user.DisplayName = "Merged User";
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
        // and the computed User.Email becomes null.
        var userEmails = await ctx.UserEmails.Where(e => e.UserId == userId).ToListAsync(ct);
        ctx.UserEmails.RemoveRange(userEmails);

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

    public async Task SetLastConsentReminderSentAsync(
        Guid userId, Instant sentAt, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([userId], ct);
        if (user is null)
            return;

        user.LastConsentReminderSentAt = sentAt;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<int> GetRejectedGoogleEmailCountAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Users.CountAsync(u => u.GoogleEmailStatus == GoogleEmailStatus.Rejected, ct);
    }

    public async Task<IReadOnlyList<Guid>> GetAccountsDueForAnonymizationAsync(
        Instant now, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Users
            .AsNoTracking()
            .Where(u => u.DeletionScheduledFor != null
                && u.DeletionScheduledFor <= now
                && (u.DeletionEligibleAfter == null || u.DeletionEligibleAfter <= now))
            .Select(u => u.Id)
            .ToListAsync(ct);
    }

    public async Task<ExpiredDeletionAnonymizationResult?> ApplyExpiredDeletionAnonymizationAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var user = await ctx.Users
            .Include(u => u.UserEmails)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null)
            return null;

        var originalEmail = user.GetEffectiveEmail();
        var originalDisplayName = user.DisplayName;
        var preferredLanguage = user.PreferredLanguage;

        user.DisplayName = "Deleted User";
        user.ProfilePictureUrl = null;
        user.PhoneNumber = null;
        user.PhoneNumberConfirmed = false;

        // Remove all verified / unverified email addresses associated with the
        // account so the unique index does not block future accounts from
        // reusing the same addresses and so the anonymized row cannot be
        // discovered by email lookup.
        ctx.UserEmails.RemoveRange(user.UserEmails);

        // Clear deletion request fields (deletion is now complete).
        user.DeletionRequestedAt = null;
        user.DeletionScheduledFor = null;
        user.DeletionEligibleAfter = null;

        // Permanently lock out the account.
        user.LockoutEnabled = true;
        user.LockoutEnd = DateTimeOffset.MaxValue;
        user.SecurityStamp = Guid.NewGuid().ToString();

        // Clear iCal token so old calendar feed URLs stop serving data.
        user.ICalToken = null;

        await ctx.SaveChangesAsync(ct);
        return new ExpiredDeletionAnonymizationResult(
            originalEmail, originalDisplayName, preferredLanguage);
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

    public async Task<IReadOnlyList<(Guid UserId, string DisplayName, string GoogleEmail)>>
        BackfillNobodiesTeamGoogleEmailsAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

        var usersToFix = await ctx.Users
            .Where(u => EF.Property<string?>(u, "GoogleEmail") == null
                && ctx.UserEmails.Any(ue =>
                    ue.UserId == u.Id
                    && ue.IsVerified
                    && EF.Functions.ILike(ue.Email, "%@nobodies.team")))
            .ToListAsync(ct);

        var result = new List<(Guid UserId, string DisplayName, string GoogleEmail)>(usersToFix.Count);

        foreach (var user in usersToFix)
        {
            var nobodiesEmail = await ctx.UserEmails
                .Where(ue => ue.UserId == user.Id
                    && ue.IsVerified
                    && EF.Functions.ILike(ue.Email, "%@nobodies.team"))
                .Select(ue => ue.Email)
                .FirstOrDefaultAsync(ct);

            if (nobodiesEmail is not null)
            {
                ctx.Entry(user).Property<string?>("GoogleEmail").CurrentValue = nobodiesEmail;
                result.Add((user.Id, user.DisplayName, nobodiesEmail));
            }
        }

        if (result.Count > 0)
        {
            await ctx.SaveChangesAsync(ct);
        }

        return result;
    }
}
