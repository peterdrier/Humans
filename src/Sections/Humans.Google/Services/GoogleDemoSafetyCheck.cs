using Humans.Base.Demo;
using Humans.Google.Domain;

namespace Humans.Google.Services;

/// <summary>Registered only with real Google credentials: then a live sync would put demo people into real groups.</summary>
internal sealed class GoogleDemoSafetyCheck(ISyncSettingsService syncSettings) : IDemoSafetyCheck
{
    public async Task<string?> RefusalAsync(CancellationToken ct)
    {
        var live = new List<SyncServiceType>();
        foreach (var serviceType in Enum.GetValues<SyncServiceType>())
        {
            if (await syncSettings.GetModeAsync(serviceType, ct) != SyncMode.None)
                live.Add(serviceType);
        }

        return live.Count == 0
            ? null
            : $"Google sync is on for {string.Join(", ", live)} with real Google credentials, so demo people would reach real Google. Set it to None first.";
    }
}
