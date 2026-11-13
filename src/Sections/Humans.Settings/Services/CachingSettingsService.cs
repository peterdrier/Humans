using Humans.Base.Caching;
using Humans.Settings.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Settings.Services;

/// <summary>Every event row plus the global timezone: the whole of what the event reads answer from.</summary>
internal sealed record SettingsSnapshot(IReadOnlyList<EventSettingsInfo> Events, string TimeZoneId);

/// <summary>
/// Holds one <see cref="SettingsSnapshot"/> so the event reads, which nearly every section makes on nearly every
/// request, never reach the database. <see cref="Service"/> clears it before it tells the change listeners, so a
/// listener that re-reads sees the write.
/// </summary>
internal sealed class CachingSettingsService(
    IServiceScopeFactory scopeFactory,
    ILogger<CachingSettingsService> logger)
    : TrackedCache<int, SettingsSnapshot>("Settings.Events", warmOnStartup: false, logger),
      ISettingsWriteService, IEventSettingsSeeding
{
    public const string InnerServiceKey = "settings-inner";

    private const int SnapshotKey = 0;

    public Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) =>
        WithInnerAsync(inner => inner.GetValueAsync(key, cancellationToken));

    public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default) =>
        WithInnerAsync(async inner =>
        {
            await inner.SetValueAsync(key, value, cancellationToken);
            return true;
        });

    public async Task<string> GetTimeZoneIdAsync(CancellationToken cancellationToken = default) =>
        (await GetSnapshotAsync(cancellationToken)).TimeZoneId;

    public async Task<EventSettingsInfo?> GetActiveEventSettingsAsync(CancellationToken cancellationToken = default) =>
        (await GetSnapshotAsync(cancellationToken)).Events.FirstOrDefault(e => e.Status == EventSettingsStatus.Active);

    public async Task<EventSettingsInfo?> GetEventSettingsByIdAsync(
        Guid id, CancellationToken cancellationToken = default) =>
        (await GetSnapshotAsync(cancellationToken)).Events.FirstOrDefault(e => e.Id == id);

    public async Task<IReadOnlyList<EventSettingsInfo>> GetAllEventSettingsAsync(
        CancellationToken cancellationToken = default) =>
        (await GetSnapshotAsync(cancellationToken)).Events;

    public Task<EventSettingsSaveResult> SaveEventSettingsAsync(
        EventSettingsInfo settings, Guid actorUserId, CancellationToken cancellationToken = default) =>
        WithInnerAsync(inner => inner.SaveEventSettingsAsync(settings, actorUserId, cancellationToken));

    public Task<EventSettingsSaveResult> SaveTimeZoneAsync(
        string timeZoneId, Guid actorUserId, CancellationToken cancellationToken = default) =>
        WithInnerAsync(inner => inner.SaveTimeZoneAsync(timeZoneId, actorUserId, cancellationToken));

    public Task<EventSettingsSaveResult> ActivateEventSettingsAsync(
        Guid eventSettingsId, Guid actorUserId, CancellationToken cancellationToken = default) =>
        WithInnerAsync(inner => inner.ActivateEventSettingsAsync(eventSettingsId, actorUserId, cancellationToken));

    public Task CreateActiveEventAsync(EventSettingsInfo settings, CancellationToken cancellationToken = default) =>
        WithInnerAsync(async inner =>
        {
            await ((IEventSettingsSeeding)inner).CreateActiveEventAsync(settings, cancellationToken);
            return true;
        });

    public Task<int> DeleteEventAsync(Guid id, CancellationToken cancellationToken = default) =>
        WithInnerAsync(inner => ((IEventSettingsSeeding)inner).DeleteEventAsync(id, cancellationToken));

    protected override async ValueTask<SettingsSnapshot?> LoadRowAsync(int key, CancellationToken ct) =>
        await WithInnerAsync(async inner => new SettingsSnapshot(
            await inner.GetAllEventSettingsAsync(ct), await inner.GetTimeZoneIdAsync(ct)));

    private async Task<SettingsSnapshot> GetSnapshotAsync(CancellationToken ct) => (await GetAsync(SnapshotKey, ct))!;

    private async Task<T> WithInnerAsync<T>(Func<ISettingsWriteService, Task<T>> work)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<ISettingsWriteService>(InnerServiceKey);
        return await work(inner);
    }
}
