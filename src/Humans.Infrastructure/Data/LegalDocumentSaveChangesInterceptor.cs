using System.Runtime.CompilerServices;
using Humans.Application.Interfaces.Caching;
using Humans.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Data;

/// <summary>
/// T-04 cache-migration sprint. EF Core <see cref="SaveChangesInterceptor"/>
/// that signals the global Legal-document cache to flush whenever a
/// persisted write touches <c>legal_documents</c> or <c>document_versions</c>.
/// </summary>
/// <remarks>
/// <para>
/// The Legal cache is bag-shaped — the cached unit is the whole set of
/// active+required documents — so the interceptor performs a wholesale
/// clear via <see cref="ILegalDocumentCacheInvalidator.InvalidateAll"/>.
/// Documents are written from <c>AdminLegalDocumentService</c>
/// (create/update/archive/version-summary) and <c>LegalDocumentSyncService</c>
/// (version add, sync touch); both flow through EF, so this single
/// interceptor catches the full write surface.
/// </para>
/// <para>
/// Mirrors <c>UserInfoSaveChangesInterceptor</c>: snapshot the
/// "has-legal-mutation" flag in <c>SavingChangesAsync</c> (before EF flips
/// Added/Modified → Unchanged and Deleted → Detached), consume it in
/// <c>SavedChangesAsync</c> (after commit, so the cache rebuilds against
/// committed data), clear the snapshot on failure so a retry doesn't fire
/// stale invalidation. Resolve the invalidator lazily through
/// <see cref="IServiceProvider"/> to avoid closing a DI cycle and swallow
/// invalidator exceptions — the write has already committed; the next
/// cache miss reloads from DB.
/// </para>
/// </remarks>
public sealed class LegalDocumentSaveChangesInterceptor(
    IServiceProvider services,
    ILogger<LegalDocumentSaveChangesInterceptor> logger) : SaveChangesInterceptor
{
    // Per-context snapshot collected in SavingChangesAsync (before commit, while
    // Deleted entries are still in the ChangeTracker) and consumed in
    // SavedChangesAsync (after commit, when Added/Modified → Unchanged and
    // Deleted → Detached). ConditionalWeakTable uses reference equality on
    // DbContext so concurrent factory-created contexts never collide; entries
    // are GC'd with their context, so the table can't leak even if
    // SavedChangesAsync never fires.
    private readonly ConditionalWeakTable<DbContext, object> _pending = new();
    private static readonly object PendingMarker = new();

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context && HasLegalDocumentMutation(context))
        {
            // AddOrUpdate: a context being reused for multiple SaveChanges in
            // sequence overwrites the prior marker — the prior snapshot will
            // have been consumed in its own SavedChangesAsync already.
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
            var invalidator = services.GetService<ILegalDocumentCacheInvalidator>();
            if (invalidator is not null)
            {
                try
                {
                    invalidator.InvalidateAll();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger.LogError(ex,
                        "LegalDocumentSaveChangesInterceptor: cache invalidation failed after SaveChanges");
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

    private static bool HasLegalDocumentMutation(DbContext context)
    {
        // Run BEFORE SaveChanges (from SavingChangesAsync) — Added/Modified
        // entries are still pre-commit and Deleted entries are still in the
        // ChangeTracker. After SaveChanges they flip to Unchanged/Detached,
        // which is why we snapshot here.
        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State == EntityState.Unchanged || entry.State == EntityState.Detached)
                continue;

            if (entry.Entity is LegalDocument or DocumentVersion)
                return true;
        }
        return false;
    }
}
