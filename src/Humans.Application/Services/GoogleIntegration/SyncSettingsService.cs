using NodaTime;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Application.Interfaces.GoogleIntegration;

namespace Humans.Application.Services.GoogleIntegration;

/// <summary>Per-service sync-mode settings (Drive/Groups/Discord). Owns sync_service_settings via repository.</summary>
public sealed class SyncSettingsService : ISyncSettingsService
{
    private readonly ISyncSettingsRepository _repository;
    private readonly IClock _clock;

    public SyncSettingsService(ISyncSettingsRepository repository, IClock clock)
    {
        _repository = repository;
        _clock = clock;
    }

    public async Task<IReadOnlyList<SyncServiceSettingsInfo>> GetAllAsync(CancellationToken ct = default)
    {
        var rows = await _repository.GetAllAsync(ct);
        return rows.Select(CreateSyncServiceSettingsInfo).ToList();
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

    private static SyncServiceSettingsInfo CreateSyncServiceSettingsInfo(SyncServiceSettings settings) =>
        new(
            settings.Id,
            settings.ServiceType,
            settings.SyncMode,
            settings.UpdatedAt,
            settings.UpdatedByUserId);
}
