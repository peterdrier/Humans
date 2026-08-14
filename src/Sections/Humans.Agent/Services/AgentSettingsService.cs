using Humans.Agent.Contracts;
using Humans.Application.Interfaces;
using Humans.Agent.Data;
using Humans.Agent.Services.Stores;
using NodaTime;
using Humans.Agent.Domain;

namespace Humans.Agent.Services;

internal sealed class AgentSettingsService(IAgentRepository repo, IAgentSettingsStore store, IClock clock)
    : IAgentSettingsService, IAgentAvailability
{
    public AgentSettingsDto Current => ToDto(store.Current);

    /// <summary>
    /// The whole of Shell's read of this section: the help widget shows the assistant panel
    /// when this is true, and the two agent health checks skip their probes when it is false.
    /// </summary>
    public bool IsEnabled => store.Current.Enabled;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var row = await repo.GetSettingsAsync(cancellationToken);
        if (row is not null)
            store.Set(row);
    }

    public async Task UpdateAsync(Action<AgentSettings> mutator, CancellationToken cancellationToken)
    {
        var row = await repo.UpdateSettingsAsync(mutator, clock.GetCurrentInstant(), cancellationToken);
        store.Set(row);
    }

    private static AgentSettingsDto ToDto(AgentSettings settings) => new(
        settings.Enabled,
        settings.Model,
        settings.PreloadConfig,
        settings.DailyMessageCap,
        settings.HourlyMessageCap,
        settings.DailyTokenCap,
        settings.RetentionDays,
        settings.UpdatedAt);
}
