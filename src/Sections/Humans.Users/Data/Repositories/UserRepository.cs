using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Users.Contracts;

namespace Humans.Users.Data.Repositories;

internal sealed partial class UserRepository : IUserRepository
{
    private readonly IDbContextFactory<UsersDbContext> _factory;
    private readonly IClock _clock;

    public UserRepository(IDbContextFactory<UsersDbContext> factory, IClock clock)
    {
        _factory = factory;
        _clock = clock;
    }

    public UserRepository(IDbContextFactory<UsersDbContext> factory)
        : this(factory, SystemClock.Instance)
    {
    }

    public async Task<User?> GetByIdAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Id == userId, ct);
    }

    public async Task<IReadOnlyList<User>> GetAllAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Users
            .AsNoTracking()
            .ToListAsync(ct);
    }

    private static string EscapeLikePattern(string value)
        => value
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");

    public async Task<bool> SetPreferredLanguageAsync(
        Guid userId, string preferredLanguage, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([userId], ct);
        if (user is null)
            return false;

        user.PreferredLanguage = preferredLanguage;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetLastLoginAsync(
        Guid userId, Instant at, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([userId], ct);
        if (user is null)
            return false;

        user.LastLoginAt = at;
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetDeletionPendingAsync(
        Guid userId, Instant requestedAt, Instant scheduledFor, Instant? eligibleAfter,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([userId], ct);
        if (user is null)
            return false;

        user.DeletionRequestedAt = requestedAt;
        user.DeletionScheduledFor = scheduledFor;
        user.DeletionEligibleAfter = eligibleAfter;
        await ResyncStateInContextAsync(ctx, user, ct);
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
        await ResyncStateInContextAsync(ctx, user, ct);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetSuspensionAsync(
        Guid userId, bool suspended, bool adminSuspension, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([userId], ct);
        if (user is null)
            return false;

        // Read-only: the classifier needs the profile's names and legacy RejectedAt.
        var profile = await ctx.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);
        user.State = UserStateEvaluator.Classify(
            user, profile,
            isSuspended: suspended && !adminSuspension,
            isAdminSuspended: suspended && adminSuspension);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetRejectedAsync(Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([userId], ct);
        if (user is null)
            return false;

        var profile = await ctx.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, ct);
        user.State = UserStateEvaluator.Classify(
            user, profile,
            isSuspended: user.State == UserState.Suspended,
            isAdminSuspended: user.State == UserState.AdminSuspended,
            isRejected: true);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    // Caller owns SaveChangesAsync so this composes with other field mutations in one round-trip.
    private static async Task ResyncStateInContextAsync(
        UsersDbContext ctx, User user, CancellationToken ct)
    {
        var profile = await ctx.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == user.Id, ct);
        user.State = UserStateEvaluator.Classify(user, profile);
    }

    public async Task<bool> AnonymizeForMergeAsync(
        Guid sourceUserId, Guid targetUserId, Instant now,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var user = await ctx.Users.FindAsync([sourceUserId], ct);
        if (user is null)
            return false;

        user.MergedToUserId = targetUserId;
        user.MergedAt = now;

        user.DisplayName = "Merged User";

        // Overwrite the User-side names with the fold's tombstone labels, so the resolver
        // (User.BurnerName) cannot render the merged human's real name.
        user.BurnerName = "Merged User";
        user.FirstName = "Merged";
        user.LastName = "User";

        // Scrub the legacy Identity email/username: the address already moved to the survivor,
        // so it would be redundant PII kept forever. A unique sentinel keeps Identity uniqueness.
        var tombstoneAddress = $"merged-{sourceUserId:N}@merged.local";
        user.Email = tombstoneAddress;
        user.UserName = tombstoneAddress;
        user.NormalizedUserName = tombstoneAddress.ToUpperInvariant();
        // NormalizedEmail is a being-removed shadow column (HUM0010 forbids the C# property).
        ctx.Entry(user).Property("NormalizedEmail").CurrentValue = tombstoneAddress.ToUpperInvariant();

        user.ProfilePictureUrl = null;
        user.PhoneNumber = null;
        user.PhoneNumberConfirmed = false;

        // Archived via merge, not deletion.
        user.DeletionRequestedAt = null;
        user.DeletionScheduledFor = null;
        user.DeletionEligibleAfter = null;

        user.LockoutEnabled = true;
        user.LockoutEnd = DateTimeOffset.MaxValue;
        user.SecurityStamp = Guid.NewGuid().ToString();

        await ResyncStateInContextAsync(ctx, user, ct);
        await ctx.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<Guid>> GetUserIdsWithExternalLoginsAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        return await ctx.Set<IdentityUserLogin<Guid>>()
            .AsNoTracking()
            .Select(l => l.UserId)
            .Distinct()
            .ToListAsync(ct);
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

    public async Task<int> DeleteUsersAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return 0;

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        await using var tx = await ctx.Database.BeginTransactionAsync(ct);

        await ctx.Set<IdentityUserLogin<Guid>>()
            .Where(l => userIds.Contains(l.UserId))
            .ExecuteDeleteAsync(ct);

        var deleted = await ctx.Users
            .Where(u => userIds.Contains(u.Id))
            .ExecuteDeleteAsync(ct);

        await tx.CommitAsync(ct);
        return deleted;
    }

    public async Task<IReadOnlyDictionary<Guid, IReadOnlyList<(string Provider, string ProviderKey)>>>
        GetExternalLoginsByUserIdsAsync(
            IReadOnlyCollection<Guid> userIds, CancellationToken ct = default)
    {
        if (userIds.Count == 0)
            return new Dictionary<Guid, IReadOnlyList<(string, string)>>();

        await using var ctx = await _factory.CreateDbContextAsync(ct);
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

    public async Task ReassignLoginsToUserAsync(
        Guid sourceUserId, Guid targetUserId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);

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
        var user = await ctx.Users.FirstOrDefaultAsync(u => u.Id == userId, ct);

        if (user is null)
            return null;

        var originalEmail = user.Email;
        var originalBurnerName = user.BurnerName ?? string.Empty;
        var preferredLanguage = user.PreferredLanguage;

        user.DisplayName = UserInfo.GdprAnonymizedBurnerName;

        // The resolver reads User.BurnerName only, so a blank one would render empty instead of
        // the "Deleted User" tombstone.
        user.BurnerName = UserInfo.GdprAnonymizedBurnerName;
        user.FirstName = "Deleted";
        user.LastName = "User";

        // Scrub the legacy Identity email/username too; the UserEmail rows are already gone.
        var tombstoneAddress = $"deleted-{userId:N}@deleted.local";
        user.Email = tombstoneAddress;
        user.UserName = tombstoneAddress;
        user.NormalizedUserName = tombstoneAddress.ToUpperInvariant();
        // NormalizedEmail is a being-removed shadow column (HUM0010 forbids the C# property).
        ctx.Entry(user).Property("NormalizedEmail").CurrentValue = tombstoneAddress.ToUpperInvariant();

        user.ProfilePictureUrl = null;
        user.PhoneNumber = null;
        user.PhoneNumberConfirmed = false;

        // The frozen re-consent stamp goes too.
        user.LastConsentReminderSentAt = null;

        // External logins go too, or the erased account could still sign in.
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

        await ResyncStateInContextAsync(ctx, user, ct);
        await ctx.SaveChangesAsync(ct);
        return new ExpiredDeletionAnonymizationResult(
            originalEmail, originalBurnerName, preferredLanguage);
    }

    public async Task<IReadOnlyList<EventParticipation>> GetEventParticipationsByUserIdAsync(
        Guid userId, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
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

        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var idList = userIds is IList<Guid> list ? list : userIds.ToList();
        var rows = await ctx.EventParticipations
            .AsNoTracking()
            .Where(ep => idList.Contains(ep.UserId))
            .ToListAsync(ct);

        return rows
            .GroupBy(ep => ep.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<EventParticipation>)g.ToList());
    }
}
