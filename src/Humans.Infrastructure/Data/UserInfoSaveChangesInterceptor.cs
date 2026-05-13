using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Data;

/// <summary>
/// Issue #703. EF Core <see cref="SaveChangesInterceptor"/> that catches
/// Identity-machinery and EF-direct writes to UserInfo-contributing tables
/// and signals the affected userIds to the <see cref="IUserInfoInvalidator"/>.
/// </summary>
/// <remarks>
/// <para>
/// Scope is intentionally narrow: only User-section tables that get written
/// outside of <c>IUserService</c> (and therefore bypass the
/// <c>CachingUserService</c> decorator) need to be caught here. That is:
/// </para>
/// <list type="bullet">
///   <item><c>users</c> — UserManager.UpdateAsync (LastLoginAt bump, password reset, lockout).</item>
///   <item><c>user_emails</c> — OAuth callback creating verified-email rows.</item>
///   <item><c>event_participations</c> — defensive coverage for any direct-repo write.</item>
///   <item>AspNet <c>user_logins</c> — OAuth callback creating login rows.</item>
/// </list>
/// <para>
/// Profile-section tables (<c>profiles</c>, <c>contact_fields</c>,
/// <c>profile_languages</c>, <c>volunteer_history_entries</c>) are
/// intentionally NOT handled here. Every write to those flows through
/// <c>CachingProfileService</c>, whose <c>RefreshEntryAsync</c> already
/// invalidates UserInfo via <c>IUserInfoInvalidator</c>. Handling them here
/// too would require resolving the owning userId from a child entity's
/// ProfileId, which (a) the decorator already knows for free, and (b) means
/// the interceptor has to crack the EF ChangeTracker / fall back to a SELECT.
/// Single source of truth lives in the decorator.
/// </para>
/// <para>
/// Invalidation fires AFTER <c>SavedChangesAsync</c> so the cache rebuilds
/// against committed data. Failure to invalidate is logged but not propagated
/// — the write has already committed; the next cache miss reloads from DB.
/// </para>
/// </remarks>
public sealed class UserInfoSaveChangesInterceptor : SaveChangesInterceptor
{
    // Resolve the invalidator lazily through IServiceProvider on each save —
    // a direct ctor dep would close a DI cycle (this interceptor is consumed
    // by IDbContextFactory options resolution, and the invalidator resolves
    // to CachingUserService which depends on Singleton repositories that
    // themselves depend on IDbContextFactory).
    private readonly IServiceProvider _services;
    private readonly ILogger<UserInfoSaveChangesInterceptor> _logger;

    public UserInfoSaveChangesInterceptor(
        IServiceProvider services,
        ILogger<UserInfoSaveChangesInterceptor> logger)
    {
        _services = services;
        _logger = logger;
    }

    // No sync SavedChanges override: every write in this codebase flows
    // through SaveChangesAsync, and a sync override would have to fire the
    // invalidator as a discarded task — opening a race window where a read
    // between the sync save returning and the invalidation completing sees
    // a stale dict entry. Keeping the async path as the only path makes
    // "invalidation completes before the save call returns" structurally
    // enforced.

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        var affected = CollectAffectedUserIds(eventData.Context);
        if (affected.Count > 0)
        {
            var invalidator = _services.GetService<IUserInfoInvalidator>();
            if (invalidator is not null)
            {
                foreach (var userId in affected)
                {
                    await SafeInvalidate(invalidator, userId, cancellationToken);
                }
            }
        }
        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    private async Task SafeInvalidate(IUserInfoInvalidator invalidator, Guid userId, CancellationToken ct)
    {
        try
        {
            await invalidator.InvalidateAsync(userId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "UserInfoSaveChangesInterceptor invalidation failed for {UserId}: {ExType}",
                userId, ex.GetType().Name);
        }
    }

    private static HashSet<Guid> CollectAffectedUserIds(DbContext? context)
    {
        var affected = new HashSet<Guid>();
        if (context is null) return affected;

        // SaveChanges has run; tracked entries are now Unchanged but still in
        // the ChangeTracker. Scan every entry; pick out the User-section types
        // that bypass IUserService. Profile-section types are handled by
        // CachingProfileService.RefreshEntryAsync — see the class remarks.
        foreach (var entry in context.ChangeTracker.Entries())
        {
            switch (entry.Entity)
            {
                case User u:
                    affected.Add(u.Id);
                    break;
                case UserEmail ue:
                    affected.Add(ue.UserId);
                    break;
                case EventParticipation ep:
                    affected.Add(ep.UserId);
                    break;
                case IdentityUserLogin<Guid> uil:
                    affected.Add(uil.UserId);
                    break;
            }
        }
        return affected;
    }
}
