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
/// Catches writes to UserInfo-contributing tables that bypass IUserService
/// (Identity machinery, OAuth callbacks, direct-repo) and invalidates UserInfo.
/// Profile-section writes are handled by CachingUserService directly. See #703.
/// </summary>
public sealed class UserInfoSaveChangesInterceptor : SaveChangesInterceptor
{
    // Lazy IServiceProvider — direct ctor injection would close a DI cycle.
    private readonly IServiceProvider _services;
    private readonly ILogger<UserInfoSaveChangesInterceptor> _logger;

    // Snapshot collected pre-commit (Deleted still tracked) and consumed post-commit.
    private readonly ConditionalWeakTable<DbContext, HashSet<Guid>> _pending = new();

    public UserInfoSaveChangesInterceptor(
        IServiceProvider services,
        ILogger<UserInfoSaveChangesInterceptor> logger)
    {
        _services = services;
        _logger = logger;
    }

    // Async-only: a sync override would have to fire invalidation as a discarded task and race.

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context)
        {
            var affected = CollectAffectedUserIds(context);
            if (affected.Count > 0)
            {
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
