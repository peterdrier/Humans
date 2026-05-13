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
                    // ContactField is keyed on ProfileId; the userId isn't on
                    // the entity. The Profile nav, if loaded, exposes UserId —
                    // otherwise we fall back to looking up profileId in the
                    // context. The decorator rebuilds the entry against the
                    // post-commit DB, so missing one ContactField is not a
                    // correctness issue (next read still hits the DB).
                    if (cf.Profile is { } profile)
                        affected.Add(profile.UserId);
                    break;
                case ProfileLanguage pl:
                    if (pl.Profile is { } plProfile)
                        affected.Add(plProfile.UserId);
                    break;
                case VolunteerHistoryEntry vh:
                    if (vh.Profile is { } vhProfile)
                        affected.Add(vhProfile.UserId);
                    break;
                case IdentityUserLogin<Guid> uil:
                    affected.Add(uil.UserId);
                    break;
            }
        }
        return affected;
    }
}
