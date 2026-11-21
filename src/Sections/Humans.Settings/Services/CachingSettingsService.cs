using Humans.Base.Caching;
using Humans.Base.Interfaces;
using NodaTime;
using Humans.Settings.Contracts;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Settings.Services;

/// <summary>Every event row, the global timezone and every key/value row: the whole of what the reads answer from.</summary>
internal sealed record SettingsSnapshot(
    IReadOnlyList<EventSettingsInfo> Events, string TimeZoneId, IReadOnlyDictionary<string, string> Values);

/// <summary>
/// Holds one <see cref="SettingsSnapshot"/> so the event reads, which nearly every section makes on nearly every
/// request, and the key/value reads, which jobs make every minute, never reach the database. It loads at startup, and <see cref="Service"/> reloads it before it tells the
/// change listeners, so a listener that re-reads sees the write.
/// </summary>
internal sealed class CachingSettingsService(
    IServiceScopeFactory scopeFactory,
    ILogger<CachingSettingsService> logger)
    : TrackedCache<int, SettingsSnapshot>("Settings.Events", warmOnStartup: true, logger),
      ISettingsWriteService, IEventSettingsSeeding, IOrgTimeZone
{
    public const string InnerServiceKey = "settings-inner";

    private const int SnapshotKey = 0;

    public async Task<string?> GetValueAsync(string key, CancellationToken cancellationToken = default) =>
        (await GetSnapshotAsync(cancellationToken)).Values.GetValueOrDefault(key);

    public async Task<IReadOnlyDictionary<string, string>> GetAllValuesAsync(CancellationToken cancellationToken = default) =>
        (await GetSnapshotAsync(cancellationToken)).Values;

    public Task SetValueAsync(string key, string value, CancellationToken cancellationToken = default) =>
        WithInnerAsync(async inner =>
        {
            await inner.SetValueAsync(key, value, cancellationToken);
            return true;
        });

    public async Task<string> GetTimeZoneIdAsync(CancellationToken cancellationToken = default) =>
        (await GetSnapshotAsync(cancellationToken)).TimeZoneId;

    public EventSettingsInfo? GetActiveEventSettings() =>
        CurrentSnapshot().Events.FirstOrDefault(e => e.Status == EventSettingsStatus.Active);

    /// <summary>The id is validated on save, so the UTC fallback only covers a hand-edited row.</summary>
    public DateTimeZone Zone => DateTimeZoneProviders.Tzdb.GetZoneOrNull(CurrentSnapshot().TimeZoneId) ?? DateTimeZone.Utc;

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

    /// <summary>Called by every save before it notifies listeners, so the plain reads never see an empty cache.</summary>
    public Task ReloadAsync() => ReplaceAsync(SnapshotKey, CancellationToken.None);

    protected override async Task WarmAllAsync(Action<int, SettingsSnapshot> publish, CancellationToken ct) =>
        publish(SnapshotKey, (await LoadRowAsync(SnapshotKey, ct))!);

    protected override async ValueTask<SettingsSnapshot?> LoadRowAsync(int key, CancellationToken ct) =>
        await WithInnerAsync(async inner => new SettingsSnapshot(
            await inner.GetAllEventSettingsAsync(ct),
            await inner.GetTimeZoneIdAsync(ct),
            await inner.GetAllValuesAsync(ct)));

    private SettingsSnapshot CurrentSnapshot() =>
        TryGet(SnapshotKey, out var snapshot)
            ? snapshot
            : throw new InvalidOperationException("The settings snapshot loads at startup and after every save; it is missing.");

    private async Task<SettingsSnapshot> GetSnapshotAsync(CancellationToken ct) => (await GetAsync(SnapshotKey, ct))!;

    private async Task<T> WithInnerAsync<T>(Func<ISettingsWriteService, Task<T>> work)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var inner = scope.ServiceProvider.GetRequiredKeyedService<ISettingsWriteService>(InnerServiceKey);
        return await work(inner);
    }
}
