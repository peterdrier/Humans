using Humans.GoogleIntegration.Contracts;
using Microsoft.EntityFrameworkCore;
using NodaTime;

namespace Humans.GoogleIntegration.Data;

internal sealed class SyncSettingsRepository(IDbContextFactory<GoogleIntegrationDbContext> factory) : ISyncSettingsRepository
{
    public async Task<IReadOnlyList<SyncServiceSettings>> GetAllAsync(CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        return await ctx.SyncServiceSettings
            .AsNoTracking()
            .ToListAsync(ct);
    }

    public async Task<SyncMode> GetModeAsync(SyncServiceType serviceType, CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var mode = await ctx.SyncServiceSettings
            .AsNoTracking()
            .Where(s => s.ServiceType == serviceType)
            .Select(s => (SyncMode?)s.SyncMode)
            .FirstOrDefaultAsync(ct);
        return mode ?? SyncMode.None;
    }

    public async Task<SyncServiceSettings?> UpdateModeAsync(
        SyncServiceType serviceType,
        SyncMode mode,
        Guid actorUserId,
        Instant updatedAt,
        CancellationToken ct = default)
    {
        await using var ctx = await factory.CreateDbContextAsync(ct);
        var setting = await ctx.SyncServiceSettings
            .FirstOrDefaultAsync(s => s.ServiceType == serviceType, ct);
        if (setting is null) return null;

        setting.SyncMode = mode;
        setting.UpdatedAt = updatedAt;
        setting.UpdatedByUserId = actorUserId;
        await ctx.SaveChangesAsync(ct);
        return setting;
    }
}
