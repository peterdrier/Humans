using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Humans.Application.Interfaces.Camps;
using Humans.Domain.Entities;

namespace Humans.Infrastructure.Data;

/// <summary>
/// T-06 (2026-05-16 cache-migration plan). EF Core
/// <see cref="SaveChangesInterceptor"/> that catches every persisted mutation
/// to Camps-section tables and signals the affected camp ids — or settings —
/// to the <see cref="ICampInfoInvalidator"/>.
/// </summary>
/// <remarks>
/// <para>
/// Watch set (all owned by <c>CampService</c> / <c>CampRoleService</c>):
/// </para>
/// <list type="bullet">
///   <item><c>camps</c> — slug / contact / flags changes.</item>
///   <item><c>camp_seasons</c> — season lifecycle (status, blurbs, EeSlotCount).</item>
///   <item><c>camp_leads</c> — Add / Remove / merge-reassign.</item>
///   <item><c>camp_historical_names</c>.</item>
///   <item><c>camp_images</c>.</item>
///   <item><c>camp_settings</c> — singleton settings slot.</item>
///   <item><b><c>camp_members</c></b> — <c>HasEarlyEntry</c> /
///     <c>Status</c> flips drive
///     <see cref="CampSeasonInfo.EeGrantedCount"/>. Without this watch the
///     cached EE-granted count is stale until process restart (historical
///     drift). The invariant is documented on the
///     <see cref="CampInfo"/> projection.</item>
/// </list>
/// <para>
/// For mutations where the camp id is reachable via a tracked navigation
/// (<c>CampSeason.CampId</c>, <c>CampLead.CampId</c>, ...), we use it
/// directly. For <c>CampMember</c>, we fall back to a database lookup of
/// <c>CampSeason.CampId</c> from the cached repository (rare path — only
/// fires on EE / status flips).
/// </para>
/// <para>
/// Invalidation runs AFTER <c>SavedChangesAsync</c>, so a failure is logged
/// and swallowed; the next cache miss reloads from the DB.
/// </para>
/// </remarks>
public sealed class CampInfoSaveChangesInterceptor : SaveChangesInterceptor
{
    private readonly IServiceProvider _services;
    private readonly ILogger<CampInfoSaveChangesInterceptor> _logger;
    private readonly ConditionalWeakTable<DbContext, Pending> _pending = new();

    public CampInfoSaveChangesInterceptor(
        IServiceProvider services,
        ILogger<CampInfoSaveChangesInterceptor> logger)
    {
        _services = services;
        _logger = logger;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context)
        {
            var snapshot = Collect(context);
            if (snapshot.CampIds.Count > 0 || snapshot.MemberSeasonIds.Count > 0 || snapshot.SettingsTouched)
            {
                _pending.AddOrUpdate(context, snapshot);
            }
        }
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    public override async ValueTask<int> SavedChangesAsync(
        SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is { } context && _pending.TryGetValue(context, out var snapshot))
        {
            _pending.Remove(context);
            var invalidator = _services.GetService<ICampInfoInvalidator>();
            if (invalidator is not null)
            {
                if (snapshot.SettingsTouched)
                {
                    await SafeInvalidateSettings(invalidator, cancellationToken);
                }
                foreach (var campId in snapshot.CampIds)
                {
                    await SafeInvalidateCamp(invalidator, campId, cancellationToken);
                }
                if (snapshot.MemberSeasonIds.Count > 0)
                {
                    await ResolveMemberCampIdsAndInvalidateAsync(
                        context, invalidator, snapshot.MemberSeasonIds,
                        snapshot.CampIds, cancellationToken);
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

    private async Task SafeInvalidateCamp(ICampInfoInvalidator invalidator, Guid campId, CancellationToken ct)
    {
        try
        {
            await invalidator.InvalidateCampAsync(campId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "CampInfoSaveChangesInterceptor invalidation failed for camp {CampId}: {ExType}",
                campId, ex.GetType().Name);
        }
    }

    private async Task SafeInvalidateSettings(ICampInfoInvalidator invalidator, CancellationToken ct)
    {
        try
        {
            await invalidator.InvalidateSettingsAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(
                "CampInfoSaveChangesInterceptor settings invalidation failed: {ExType}",
                ex.GetType().Name);
        }
    }

    private async Task ResolveMemberCampIdsAndInvalidateAsync(
        DbContext context,
        ICampInfoInvalidator invalidator,
        HashSet<Guid> memberSeasonIds,
        HashSet<Guid> alreadyInvalidated,
        CancellationToken ct)
    {
        // Resolve season → camp via the same context. AsNoTracking so we
        // don't disturb the tracker the caller just saved through.
        var campIds = await context.Set<CampSeason>()
            .AsNoTracking()
            .Where(s => memberSeasonIds.Contains(s.Id))
            .Select(s => s.CampId)
            .Distinct()
            .ToListAsync(ct);

        foreach (var campId in campIds)
        {
            if (alreadyInvalidated.Add(campId))
            {
                await SafeInvalidateCamp(invalidator, campId, ct);
            }
        }
    }

    private static Pending Collect(DbContext context)
    {
        var campIds = new HashSet<Guid>();
        var memberSeasonIds = new HashSet<Guid>();
        var settingsTouched = false;

        foreach (var entry in context.ChangeTracker.Entries())
        {
            if (entry.State == EntityState.Unchanged || entry.State == EntityState.Detached)
                continue;

            switch (entry.Entity)
            {
                case Camp c:
                    campIds.Add(c.Id);
                    break;
                case CampSeason s:
                    campIds.Add(s.CampId);
                    break;
                case CampLead l:
                    campIds.Add(l.CampId);
                    break;
                case CampHistoricalName h:
                    campIds.Add(h.CampId);
                    break;
                case CampImage i:
                    campIds.Add(i.CampId);
                    break;
                case CampSettings:
                    settingsTouched = true;
                    break;
                case CampMember m:
                    // EeGrantedCount cross-table invariant — see class
                    // remarks. CampSeason → CampId lookup happens after
                    // SaveChanges so we don't race the writer's own load.
                    memberSeasonIds.Add(m.CampSeasonId);
                    break;
            }
        }

        return new Pending(campIds, memberSeasonIds, settingsTouched);
    }

    private sealed record Pending(
        HashSet<Guid> CampIds,
        HashSet<Guid> MemberSeasonIds,
        bool SettingsTouched);
}
