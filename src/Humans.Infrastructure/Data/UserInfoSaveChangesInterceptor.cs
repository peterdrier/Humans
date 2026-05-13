using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Data;

/// <summary>
/// Issue #703. EF Core <see cref="SaveChangesInterceptor"/> that catches every
/// write to a table contributing to <see cref="Humans.Application.UserInfo"/>
/// and signals the affected userIds to the
/// <see cref="IUserInfoInvalidator"/>. Closes the gap left by Identity-machinery
/// write paths (<c>UserManager.UpdateAsync</c>, sign-in <c>LastLoginAt</c>
/// bumps, OAuth <c>UserEmail</c> creation) which bypass <c>IUserService</c>.
/// </summary>
/// <remarks>
/// <para>
/// The 8 contributing tables: <c>users</c>, <c>user_emails</c>,
/// <c>event_participations</c>, AspNet <c>user_logins</c>, <c>profiles</c>,
/// <c>contact_fields</c>, <c>profile_languages</c>,
/// <c>volunteer_history_entries</c>. Affected userIds are resolved per-entity
/// in <see cref="CollectAffectedUserIds"/>.
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

    public override int SavedChanges(SaveChangesCompletedEventData eventData, int result)
    {
        var affected = CollectAffectedUserIds(eventData.Context);
        if (affected.Count > 0)
        {
            var invalidator = _services.GetService<IUserInfoInvalidator>();
            if (invalidator is not null)
            {
                foreach (var userId in affected)
                {
                    _ = SafeInvalidate(invalidator, userId, CancellationToken.None);
                }
            }
        }
        return base.SavedChanges(eventData, result);
    }

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

        // Read tracker state AFTER SaveChanges — entities are now Unchanged.
        // ChangeTracker still references them; SaveChanges does not clear it.
        // We inspect every entry that was previously modified/added/deleted via
        // the StateAfterSaveChanges signal: easier to just inspect every
        // tracked entry of an interesting type.
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
                case Profile p:
                    affected.Add(p.UserId);
                    break;
                case ContactField cf:
                    {
                        // ContactField is keyed on ProfileId; userId isn't on
                        // the entity. Resolution order: (1) loaded Profile nav,
                        // (2) Profile entity tracked in the same context,
                        // (3) single small SELECT against Profile by Id.
                        // (3) is the rare-case fallback for write paths like
                        // ContactFieldRepository.BatchSaveAsync that attach
                        // detached entities into a fresh DbContext without
                        // loading the Profile navigation.
                        var uid = cf.Profile?.UserId ?? ResolveProfileOwner(context, cf.ProfileId);
                        if (uid is { } id) affected.Add(id);
                        break;
                    }
                case ProfileLanguage pl:
                    {
                        var uid = pl.Profile?.UserId ?? ResolveProfileOwner(context, pl.ProfileId);
                        if (uid is { } id) affected.Add(id);
                        break;
                    }
                case VolunteerHistoryEntry vh:
                    {
                        var uid = vh.Profile?.UserId ?? ResolveProfileOwner(context, vh.ProfileId);
                        if (uid is { } id) affected.Add(id);
                        break;
                    }
                case IdentityUserLogin<Guid> uil:
                    affected.Add(uil.UserId);
                    break;
            }
        }
        return affected;
    }

    /// <summary>
    /// Resolves the owning userId for a profileId when the Profile nav is not
    /// loaded on the changed entity. Checks the ChangeTracker first (no DB hit
    /// if another touched entry already carries the Profile), then falls back
    /// to a single AsNoTracking SELECT. Sync is acceptable here: the Profile
    /// table is small, this only fires on write paths that bypass nav loading,
    /// and the row is hot from the just-committed write.
    /// </summary>
    private static Guid? ResolveProfileOwner(DbContext context, Guid profileId)
    {
        var tracked = context.ChangeTracker.Entries<Profile>()
            .FirstOrDefault(e => e.Entity.Id == profileId)?.Entity;
        if (tracked is not null)
            return tracked.UserId;

        return context.Set<Profile>()
            .AsNoTracking()
            .Where(p => p.Id == profileId)
            .Select(p => (Guid?)p.UserId)
            .FirstOrDefault();
    }
}
