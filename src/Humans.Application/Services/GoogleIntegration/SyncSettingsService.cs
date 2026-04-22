using NodaTime;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;

namespace Humans.Application.Services.GoogleIntegration;

/// <summary>
/// Application-layer service for per-service Google sync mode settings
/// (Drive / Groups / Discord). Owns the <c>sync_service_settings</c> table
/// through <see cref="ISyncSettingsRepository"/> — no direct EF access.
/// First service migrated to the §15 pattern as part of issue #554.
/// </summary>
public sealed class SyncSettingsService : ISyncSettingsService
{
    private readonly ISyncSettingsRepository _repository;
    private readonly IClock _clock;

    public SyncSettingsService(ISyncSettingsRepository repository, IClock clock)
    {
        _repository = repository;
        _clock = clock;
    }

    public async Task<List<SyncServiceSettings>> GetAllAsync(CancellationToken ct = default)
    {
        var rows = await _repository.GetAllAsync(ct);
        // Return a mutable List<T> to preserve the pre-migration contract.
        return rows.ToList();
    }

    public Task<SyncMode> GetModeAsync(SyncServiceType serviceType, CancellationToken ct = default)
        => _repository.GetModeAsync(serviceType, ct);

    public async Task UpdateModeAsync(
        SyncServiceType serviceType, SyncMode mode, Guid actorUserId, CancellationToken ct = default)
    {
        var updated = await _repository.UpdateModeAsync(
            serviceType, mode, actorUserId, _clock.GetCurrentInstant(), ct);
        if (!updated)
        {
            throw new InvalidOperationException($"No sync setting found for {serviceType}");
        }
    }
}
