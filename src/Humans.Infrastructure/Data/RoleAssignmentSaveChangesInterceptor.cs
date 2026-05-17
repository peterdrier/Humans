using System.Runtime.CompilerServices;
using Humans.Application.Interfaces.Caching;
using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Data;

/// <summary>
/// EF Core <see cref="SaveChangesInterceptor"/> that signals the global
/// role-assignment cache to flush whenever a persisted write touches
/// <c>role_assignments</c>. Mirrors <see cref="LegalDocumentSaveChangesInterceptor"/>:
/// snapshot the "has-role-assignment-mutation" flag in
/// <see cref="SavingChangesAsync"/> (before EF flips Added/Modified →
/// Unchanged and Deleted → Detached), consume it in
/// <see cref="SavedChangesAsync"/> (after commit), clear on failure so a
/// retry doesn't fire stale invalidation.
/// </summary>
public sealed class RoleAssignmentSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly IServiceProvider _services;
    private readonly ILogger<RoleAssignmentSaveChangesInterceptor> _logger;

    private readonly ConditionalWeakTable<DbContext, object> _pending = new();
    private static readonly object PendingMarker = new();

    public RoleAssignmentSaveChangesInterceptor(
        IServiceProvider services,
        ILogger<RoleAssignmentSaveChangesInterceptor> logger)
    {
        _services = services;
        _logger = logger;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context && HasRoleAssignmentMutation(context))
        {
            _pending.AddOrUpdate(context, PendingMarker);
        }
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context && _pending.TryGetValue(context, out _))
        {
            _pending.Remove(context);
            var invalidator = _services.GetService<IRoleAssignmentCacheInvalidator>();
            if (invalidator is not null)
            {
                try
                {
                    invalidator.InvalidateAll();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        "RoleAssignmentSaveChangesInterceptor invalidation failed: {ExType}",
                        ex.GetType().Name);
                }
            }
        }

        return base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    public override void SaveChangesFailed(DbContextErrorEventData eventData)
    {
        if (eventData.Context is { } context) _pending.Remove(context);
        base.SaveChangesFailed(eventData);
    }

    public override Task SaveChangesFailedAsync(
        DbContextErrorEventData eventData,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context) _pending.Remove(context);
        return base.SaveChangesFailedAsync(eventData, cancellationToken);
    }

    private static bool HasRoleAssignmentMutation(DbContext context)
    {
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State == EntityState.Unchanged || entry.State == EntityState.Detached)
                continue;

            if (entry.Entity is RoleAssignment)
                return true;
        }
        return false;
    }
}
