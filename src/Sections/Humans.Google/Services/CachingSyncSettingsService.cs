using Humans.Base.Caching;
using Humans.Google.Contracts;
using Humans.Google.Domain;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Google.Services;

/// <summary>
/// Holds every <c>google_sync_service_settings</c> row, which each Google sync step reads once per member. The mode
/// change is the table's only write, so it clears the cache here.
/// </summary>
internal sealed class CachingSyncSettingsService(
    IServiceScopeFactory scopeFactory,
    ILogger<CachingSyncSettingsService> logger)
    : TrackedCache<SyncServiceType, SyncServiceSettingsInfo>("Google.SyncServiceSettings", warmOnStartup: true, logger),
      ISyncSettingsService
{
    public const string InnerServiceKey = "sync-settings-inner";

    public async Task<IReadOnlyList<SyncServiceSettingsInfo>> GetAllAsync(CancellationToken ct = default)
    {
        await EnsureWarmedAsync(ct);
        return AsReadOnlyDictionary.Values.ToList();
    }

    public async Task<SyncMode> GetModeAsync(SyncServiceType serviceType, CancellationToken ct = default)
    {
        await EnsureWarmedAsync(ct);
        return TryGet(serviceType, out var row) ? row.SyncMode : SyncMode.None;
    }

    public async Task UpdateModeAsync(
        SyncServiceType serviceType, SyncMode mode, Guid actorUserId, CancellationToken ct = default)
    {
        try
        {
            await WithInnerAsync(inner => inner.UpdateModeAsync(serviceType, mode, actorUserId, ct));
        }
        finally
        {
            Clear();
        }
    }

    protected override async Task WarmAllAsync(
        Action<SyncServiceType, SyncServiceSettingsInfo> publish, CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<ISyncSettingsService>(InnerServiceKey);
        foreach (var row in await inner.GetAllAsync(ct))
            publish(row.ServiceType, row);
    }

    private async Task WithInnerAsync(Func<ISyncSettingsService, Task> work)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        await work(scope.ServiceProvider.GetRequiredKeyedService<ISyncSettingsService>(InnerServiceKey));
    }
}
