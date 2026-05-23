using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Application;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Repositories.Users;

/// <summary>EF-backed <see cref="IUserRepository"/>.</summary>
internal sealed class UserRepository(IDbContextFactory<HumansDbContext> factory) : IUserRepository
{
    // Reads — User

    public async Task<User?> GetByIdAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Users
            .AsNoTracking()
            .Include(u => u.UserEmails)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);
    }

    public async Task<IReadOnlyDictionary<Guid, User>> GetByIdsAsync(
        IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, User>();

        await using var ctx = await factory.CreateDbContextAsync(ct);
        var list = await ctx.Users
            .AsNoTracking()
            .Include(u => u.UserEmails)
            .Where(u => userIds.Contains(u.Id))
            .ToListAsync(ct);

        return list.ToDictionary(u => u.Id);
    }

    public async Task<IReadOnlyList<User>> GetAllAsync(CancellationToken ct = default)
    {
        // Include UserEmails so computed User.Email isn't null.
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Users
            .AsNoTracking()
            .Include(u => u.UserEmails)
            .ToListAsync(ct);
    }

    public async Task<User?> GetByEmailOrAlternateAsync(
        string normalizedEmail, string? alternateEmail, CancellationToken ct = default)
    {
        // ILIKE: escape '_' / '%' in input or alex_smith@... matches alexXsmith@...
        // Lookup verifies user_emails first; falls back to legacy GoogleEmail shadow column.
        var escapedEmail = EscapeLikePattern(normalizedEmail);
        var escapedAlternate = alternateEmail is null ? null : EscapeLikePattern(alternateEmail);

        await using var ctx = await factory.CreateDbContextAsync(ct);

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

        // Fallback: legacy GoogleEmail shadow column.
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

    public async Task<Guid?> GetOtherUserIdHavingGoogleEmailAsync(
        string email, Guid excludeUserId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
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

        await using var ctx = await factory.CreateDbContextAsync(ct);
        var rows = await ctx.Users
            .AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, GoogleEmail = EF.Property<string?>(u, "GoogleEmail") })
            .Where(x => x.GoogleEmail != null)
            .ToListAsync(ct);

        return rows.ToDictionary(x => x.Id, x => x.GoogleEmail!);
    }

    // Writes — User

    public async Task<bool> UpdateDisplayNameAsync(
        Guid userId, string displayName, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([userId], ct);
        if (user is null)
            return false;

        user.DisplayName = displayName;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetPreferredLanguageAsync(
        Guid userId, string preferredLanguage, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([userId], ct);
        if (user is null)
            return false;

        user.PreferredLanguage = preferredLanguage;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetICalTokenAsync(
        Guid userId, Guid token, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([userId], ct);
        if (user is null)
            return false;

        user.ICalToken = token;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> TrySetGoogleEmailAsync(
        Guid userId, string email, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
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
        await using var ctx = await factory.CreateDbContextAsync(ct);
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
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([userId], ct);
        if (user is null)
            return false;

        if (user.GoogleEmailStatus == status)
            return false;

        user.GoogleEmailStatus = status;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetDeletionPendingAsync(
        Guid userId, Instant requestedAt, Instant scheduledFor, Instant? eligibleAfter,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([userId], ct);
        if (user is null)
            return false;

        user.DeletionRequestedAt = requestedAt;
        user.DeletionScheduledFor = scheduledFor;
        user.DeletionEligibleAfter = eligibleAfter;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> ClearDeletionAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([userId], ct);
        if (user is null)
            return false;

        user.DeletionRequestedAt = null;
        user.DeletionScheduledFor = null;
        user.DeletionEligibleAfter = null;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> AnonymizeForMergeAsync(
        Guid sourceUserId, Guid targetUserId, Instant now,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([sourceUserId], ct);
        if (user is null)
            return false;

        user.MergedToUserId = targetUserId;
        user.MergedAt = now;

        user.DisplayName = "Merged User";
        user.ProfilePictureUrl = null;
        user.PhoneNumber = null;
        user.PhoneNumberConfirmed = false;

        // Archived via merge, not deletion — clear deletion fields.
        user.DeletionRequestedAt = null;
        user.DeletionScheduledFor = null;
        user.DeletionEligibleAfter = null;

        user.LockoutEnabled = true;
        user.LockoutEnd = DateTimeOffset.MaxValue;
        user.SecurityStamp = Guid.NewGuid().ToString();

        user.ICalToken = null;

        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<Guid>> GetUsersWithLoginsButNoEmailsAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        var loginUserIds = await ctx.Set<IdentityUserLogin<Guid>>()
            .AsNoTracking()
            .Select(l => l.UserId)
            .Distinct()
            .ToListAsync(ct);

        if (loginUserIds.Count == 0) return [];

        var withEmail = await ctx.UserEmails
            .AsNoTracking()
            .Where(e => loginUserIds.Contains(e.UserId))
            .Select(e => e.UserId)
            .Distinct()
            .ToListAsync(ct);

        var withEmailSet = withEmail.ToHashSet();
        return loginUserIds.Where(id => !withEmailSet.Contains(id)).ToList();
    }

    public async Task<bool> SetContactSourceIfNullAsync(
        Guid userId, ContactSource source, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([userId], ct);
        if (user is null || user.ContactSource is not null)
            return false;

        user.ContactSource = source;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<int> DeleteUsersAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return 0;

        await using var ctx = await factory.CreateDbContextAsync(ct);
        await using var tx = await ctx.Database.BeginTransactionAsync(ct);

        await ctx.UserEmails
            .Where(e => userIds.Contains(e.UserId))
            .ExecuteDeleteAsync(ct);

        await ctx.Set<IdentityUserLogin<Guid>>()
            .Where(l => userIds.Contains(l.UserId))
            .ExecuteDeleteAsync(ct);

        var deleted = await ctx.Users
            .Where(u => userIds.Contains(u.Id))
            .ExecuteDeleteAsync(ct);

        await tx.CommitAsync(ct);
        return deleted;
    }

    public async Task<int> DeleteAllExternalLoginsForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.Set<IdentityUserLogin<Guid>>()
            .Where(l => l.UserId == userId)
            .ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<(string Provider, string ProviderKey)>>>
        GetExternalLoginsByUserIdsAsync(
            IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<(string, string)>>();

        await using var ctx = await factory.CreateDbContextAsync(ct);
        var rows = await ctx.Set<IdentityUserLogin<Guid>>()
            .AsNoTracking()
            .Where(l => userIds.Contains(l.UserId))
            .Select(l => new { l.UserId, l.LoginProvider, l.ProviderKey })
            .ToListAsync(ct);

        return rows
            .GroupBy(r => r.UserId)
            .ToDictionary(
                g => g.Key,
                g => (IReadOnlyList<(string, string)>)g
                    .Select(r => (r.LoginProvider, r.ProviderKey))
                    .ToList());
    }

    public async Task<int> ReassignLoginsToUserAsync(
        Guid sourceUserId, Guid targetUserId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        var sourceLogins = await ctx.Set<IdentityUserLogin<Guid>>()
            .Where(l => l.UserId == sourceUserId)
            .ToListAsync(ct);

        // Two-pass Remove+Add: EF identity map keys on the composite PK
        // (LoginProvider, ProviderKey), so the intermediate SaveChanges is required.
        foreach (var login in sourceLogins)
        {
            ctx.Set<IdentityUserLogin<Guid>>().Remove(login);
        }

        if (sourceLogins.Count > 0)
        {
            await ctx.SaveChangesAsync(ct);
        }

        foreach (var login in sourceLogins)
        {
            ctx.Set<IdentityUserLogin<Guid>>().Add(new IdentityUserLogin<Guid>
            {
                LoginProvider = login.LoginProvider,
                ProviderKey = login.ProviderKey,
                ProviderDisplayName = login.ProviderDisplayName,
                UserId = targetUserId
            });
        }

        if (sourceLogins.Count > 0)
        {
            await ctx.SaveChangesAsync(ct);
        }

        return await ctx.Set<IdentityUserLogin<Guid>>()
            .CountAsync(l => l.UserId == targetUserId, ct);
    }

    public async Task<int> ReassignEventParticipationToUserAsync(
        Guid sourceUserId, Guid targetUserId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);

        // Collision rule (Year, UserId): keep the highest-precedence status.
        var rows = await ctx.EventParticipations
            .Where(ep => ep.UserId == sourceUserId || ep.UserId == targetUserId)
            .ToListAsync(ct);

        var byYear = rows.GroupBy(ep => ep.Year);

        foreach (var group in byYear)
        {
            var sourceRow = group.FirstOrDefault(ep => ep.UserId == sourceUserId);
            if (sourceRow is null)
                continue;

            var targetRow = group.FirstOrDefault(ep => ep.UserId == targetUserId);
            if (targetRow is null)
            {
                sourceRow.UserId = targetUserId;
                continue;
            }

            if (StatusPrecedence(sourceRow.Status) > StatusPrecedence(targetRow.Status))
            {
                targetRow.Status = sourceRow.Status;
                targetRow.Source = sourceRow.Source;
                targetRow.DeclaredAt = sourceRow.DeclaredAt;
            }
            ctx.EventParticipations.Remove(sourceRow);
        }

        await ctx.SaveChangesAsync(ct);

        return await ctx.EventParticipations
            .CountAsync(ep => ep.UserId == targetUserId, ct);
    }

    private static int StatusPrecedence(ParticipationStatus status) => status switch
    {
        ParticipationStatus.Attended => 4,
        ParticipationStatus.Ticketed => 3,
        ParticipationStatus.NoShow => 2,
        ParticipationStatus.NotAttending => 1,
        _ => 0
    };

    public async Task<string?> PurgeAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([userId], ct);
        if (user is null)
            return null;

        var displayName = user.DisplayName;

        // Free the unique index and null out User.Email.
        var userEmails = await ctx.UserEmails.Where(e => e.UserId == userId).ToListAsync(ct);
        ctx.UserEmails.RemoveRange(userEmails);

        // Drop external logins so a returning Google user can land on a fresh
        // account, not this tombstone. See nobodies-collective/Humans#661.
        var logins = await ctx.Set<IdentityUserLogin<Guid>>()
            .Where(l => l.UserId == userId)
            .ToListAsync(ct);
        ctx.Set<IdentityUserLogin<Guid>>().RemoveRange(logins);

        user.DisplayName = $"Purged ({displayName})";

        user.LockoutEnabled = true;
        user.LockoutEnd = DateTimeOffset.MaxValue;

        await ctx.SaveChangesAsync(ct);
        return displayName;
    }

    public async Task SetLastConsentReminderSentAsync(
        Guid userId, Instant sentAt, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([userId], ct);
        if (user is null)
            return;

        user.LastConsentReminderSentAt = sentAt;
        await ctx.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<Guid>> GetAccountsDueForAnonymizationAsync(
        Instant now, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
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
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var user = await ctx.Users
            .Include(u => u.UserEmails)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null)
            return null;

        var originalEmail = user.Email;
        var originalDisplayName = user.DisplayName;
        var preferredLanguage = user.PreferredLanguage;

        user.DisplayName = UserInfo.GdprAnonymizedBurnerName;
        user.ProfilePictureUrl = null;
        user.PhoneNumber = null;
        user.PhoneNumberConfirmed = false;

        // Free the unique index and prevent email-lookup discovery.
        ctx.UserEmails.RemoveRange(user.UserEmails);

        // Drop external logins for the same reason — see nobodies-collective/Humans#661.
        var logins = await ctx.Set<IdentityUserLogin<Guid>>()
            .Where(l => l.UserId == userId)
            .ToListAsync(ct);
        ctx.Set<IdentityUserLogin<Guid>>().RemoveRange(logins);

        user.DeletionRequestedAt = null;
        user.DeletionScheduledFor = null;
        user.DeletionEligibleAfter = null;

        user.LockoutEnabled = true;
        user.LockoutEnd = DateTimeOffset.MaxValue;
        user.SecurityStamp = Guid.NewGuid().ToString();

        user.ICalToken = null;

        await ctx.SaveChangesAsync(ct);
        return new ExpiredDeletionAnonymizationResult(
            originalEmail, originalDisplayName, preferredLanguage);
    }

    // Reads — EventParticipation

    public async Task<EventParticipation?> GetParticipationAsync(
        Guid userId, int year, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.EventParticipations
            .AsNoTracking()
            .FirstOrDefaultAsync(ep => ep.UserId == userId && ep.Year == year, ct);
    }

    public async Task<IReadOnlyList<EventParticipation>> GetEventParticipationsByUserIdAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.EventParticipations
            .AsNoTracking()
            .Where(ep => ep.UserId == userId)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<EventParticipation>>>
        GetEventParticipationsByUserIdsAsync(
            IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<EventParticipation>>();

        await using var ctx = await factory.CreateDbContextAsync(ct);
        var idList = userIds is IList<Guid> list ? list : userIds.ToList();
        var rows = await ctx.EventParticipations
            .AsNoTracking()
            .Where(ep => idList.Contains(ep.UserId))
            .ToListAsync(ct);

        return rows
            .GroupBy(ep => ep.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<EventParticipation>)g.ToList());
    }

    // Writes — EventParticipation

    public async Task<EventParticipation?> UpsertParticipationAsync(
        Guid userId,
        int year,
        ParticipationStatus status,
        ParticipationSource source,
        Instant? declaredAt,
        Instant? checkedInAt,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
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
            // CheckedInAt is set on the first transition into Attended and is
            // permanent afterwards. Don't overwrite an already-non-null value
            // (issue nobodies-collective/Humans#736).
            if (existing.CheckedInAt is null)
                existing.CheckedInAt = checkedInAt;
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
                CheckedInAt = checkedInAt,
            };
            ctx.EventParticipations.Add(persisted);
        }

        await ctx.SaveChangesAsync(ct);

        ctx.Entry(persisted).State = EntityState.Detached;
        return persisted;
    }

    public async Task<bool> RemoveParticipationAsync(
        Guid userId,
        int year,
        ParticipationSource requiredSource,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
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

        await using var ctx = await factory.CreateDbContextAsync(ct);
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
