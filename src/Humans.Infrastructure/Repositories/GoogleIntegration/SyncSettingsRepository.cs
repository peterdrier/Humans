using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Repositories.GoogleIntegration;

/// <summary>
/// EF-backed implementation of <see cref="ISyncSettingsRepository"/>. The only
/// non-test file that touches <c>DbContext.SyncServiceSettings</c> after the
/// Google Integration §15 migration lands.
/// Uses <see cref="IDbContextFactory{TContext}"/> so the repository can be
/// registered as Singleton while <c>HumansDbContext</c> remains Scoped.
/// </summary>
public sealed class SyncSettingsRepository : ISyncSettingsRepository
{
    private readonly IDbContextFactory<HumansDbContext> _factory;

    public SyncSettingsRepository(IDbContextFactory<HumansDbContext> factory)
    {
        _factory = factory;
    }

    public async Task<IReadOnlyList<SyncServiceSettings>> GetAllAsync(CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        // Cross-domain nav UpdatedByUser intentionally NOT loaded — callers
        // resolve display names via IUserService (design-rules §6).
        return await ctx.SyncServiceSettings
            .AsNoTracking()
            .OrderBy(s => s.ServiceType)
            .ToListAsync(ct);
    }

    public async Task<SyncMode> GetModeAsync(SyncServiceType serviceType, CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var mode = await ctx.SyncServiceSettings
            .AsNoTracking()
            .Where(s => s.ServiceType == serviceType)
            .Select(s => (SyncMode?)s.SyncMode)
            .FirstOrDefaultAsync(ct);
        return mode ?? SyncMode.None;
    }

    public async Task<bool> UpdateModeAsync(
        SyncServiceType serviceType,
        SyncMode mode,
        Guid actorUserId,
        Instant updatedAt,
        CancellationToken ct = default)
    {
        await using var ctx = await _factory.CreateDbContextAsync(ct);
        var setting = await ctx.SyncServiceSettings
            .FirstOrDefaultAsync(s => s.ServiceType == serviceType, ct);
        if (setting is null) return false;

        setting.SyncMode = mode;
        setting.UpdatedAt = updatedAt;
        setting.UpdatedByUserId = actorUserId;
        await ctx.SaveChangesAsync(ct);
        return true;
    }
}
