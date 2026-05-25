using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Stores;
using Humans.Domain.Entities;
using NodaTime;

namespace Humans.Infrastructure.Services.Agent;

public sealed class AgentSettingsService(IAgentRepository repo, IAgentSettingsStore store, IClock clock)
    : IAgentSettingsService
{
    public AgentSettingsDto Current => ToDto(store.Current);

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
