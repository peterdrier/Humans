using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Services;

public class SyncSettingsService : ISyncSettingsService
{
    private readonly HumansDbContext _dbContext;
    private readonly IClock _clock;

    public SyncSettingsService(HumansDbContext dbContext, IClock clock)
    {
        _dbContext = dbContext;
        _clock = clock;
    }

    public async Task<List<SyncServiceSettings>> GetAllAsync(CancellationToken ct = default)
    {
        return await _dbContext.SyncServiceSettings
            .Include(s => s.UpdatedByUser)
            .OrderBy(s => s.ServiceType)
            .ToListAsync(ct);
    }

    public async Task<SyncMode> GetModeAsync(SyncServiceType serviceType, CancellationToken ct = default)
    {
        var setting = await _dbContext.SyncServiceSettings
            .FirstOrDefaultAsync(s => s.ServiceType == serviceType, ct);
        return setting?.SyncMode ?? SyncMode.None;
    }

    public async Task UpdateModeAsync(SyncServiceType serviceType, SyncMode mode, Guid actorUserId, CancellationToken ct = default)
    {
        var setting = await _dbContext.SyncServiceSettings
            .FirstOrDefaultAsync(s => s.ServiceType == serviceType, ct)
            ?? throw new InvalidOperationException($"No sync setting found for {serviceType}");

        setting.SyncMode = mode;
        setting.UpdatedAt = _clock.GetCurrentInstant();
        setting.UpdatedByUserId = actorUserId;
        await _dbContext.SaveChangesAsync(ct);
    }
}
