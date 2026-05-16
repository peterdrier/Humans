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
/// Mirrors <c>UserInfoSaveChangesInterceptor</c>: resolve the invalidator
/// through <see cref="IServiceProvider"/> on each save to avoid closing a
/// DI cycle, invalidate after <c>SavedChangesAsync</c> so the cache
/// rebuilds against committed data, swallow invalidator exceptions.
/// </para>
/// </remarks>
public sealed class LegalDocumentSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly IServiceProvider _services;
    private readonly ILogger<LegalDocumentSaveChangesInterceptor> _logger;

    public LegalDocumentSaveChangesInterceptor(
        IServiceProvider services,
        ILogger<LegalDocumentSaveChangesInterceptor> logger)
    {
        _services = services;
        _logger = logger;
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context && HasLegalDocumentMutation(context))
        {
            var invalidator = _services.GetService<ILegalDocumentCacheInvalidator>();
            if (invalidator is not null)
            {
                try
                {
                    invalidator.InvalidateAll();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(
                        "LegalDocumentSaveChangesInterceptor invalidation failed: {ExType}",
                        ex.GetType().Name);
                }
            }
        }

        return await base.SavedChangesAsync(eventData, result, cancellationToken);
    }

    private static bool HasLegalDocumentMutation(DbContext context)
    {
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
