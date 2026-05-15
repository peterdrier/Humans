using System.Runtime.CompilerServices;
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
///   <item><c>communication_preferences</c> — opt-in/out toggles written directly via <c>ICommunicationPreferenceRepository</c>; rides on the User cache because the prefs collection is part of <c>UserInfo</c>.</item>
/// </list>
/// <para>
/// Profile-section tables (<c>profiles</c>, <c>contact_fields</c>,
/// <c>profile_languages</c>, <c>volunteer_history_entries</c>) are
/// intentionally NOT handled here. Every write to those flows through
/// <c>CachingUserService</c>, whose <c>RefreshEntryAsync</c> already
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

    // Per-context snapshot collected in SavingChangesAsync (before commit, while
    // Deleted entries are still in the ChangeTracker) and consumed in
    // SavedChangesAsync (after commit, when Deleted→Detached). ConditionalWeakTable
    // uses reference equality on DbContext so concurrent factory-created contexts
    // never collide; entries are GC'd with their context, so the table can't leak
    // even if SavedChangesAsync never fires.
    private readonly ConditionalWeakTable<DbContext, HashSet<Guid>> _pending = new();

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

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context)
        {
            var affected = CollectAffectedUserIds(context);
            if (affected.Count > 0)
            {
                // AddOrUpdate: a context being reused for multiple SaveChanges in
                // sequence overwrites the prior snapshot — the prior snapshot will
                // have been consumed in its own SavedChangesAsync already.
                _pending.AddOrUpdate(context, affected);
            }
        }
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context && _pending.TryGetValue(context, out var affected))
        {
            _pending.Remove(context);
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

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context is { } context) _pending.Remove(context);
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context) _pending.Remove(context);
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
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

    private static HashSet<Guid> CollectAffectedUserIds(DbContext context)
    {
        var affected = new HashSet<Guid>();

        // Run BEFORE SaveChanges (from SavingChangesAsync) — Deleted entries are
        // still in the ChangeTracker at this point. After SaveChanges they go
        // Deleted→Detached and disappear, which is why we snapshot here.
        // Scope: User-section tables that bypass IUserService (Identity machinery,
        // OAuth pipeline, direct-repo calls). Profile-section types are handled
        // by CachingUserService.RefreshEntryAsync — see the class remarks.
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State == EntityState.Unchanged || entry.State == EntityState.Detached)
                continue;

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
                case CommunicationPreference cp:
                    affected.Add(cp.UserId);
                    break;
            }
        }
        return affected;
    }
}
