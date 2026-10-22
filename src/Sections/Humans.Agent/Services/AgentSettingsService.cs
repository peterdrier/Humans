using Humans.Agent.Contracts;
using Humans.AuditLog.Contracts;
using Humans.Agent.Data;
using Humans.Agent.Services.Stores;
using NodaTime;
using Humans.Agent.Domain;

namespace Humans.Agent.Services;

internal sealed class AgentSettingsService(IAgentRepository repo, IAgentSettingsStore store, IClock clock, IAuditLogService audit)
    : IAgentSettingsService, IAgentAvailability
{
    public AgentSettingsDto Current => ToDto(store.Current);

    public bool IsEnabled => store.Current.Enabled;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        var row = await repo.GetSettingsAsync(cancellationToken);
        if (row is not null)
            store.Set(row);
    }

    public async Task UpdateAsync(Action<AgentSettings> mutator, Guid actorUserId, CancellationToken cancellationToken)
    {
        var row = await repo.UpdateSettingsAsync(mutator, clock.GetCurrentInstant(), cancellationToken);
        store.Set(row);
        await audit.LogAsync(AuditAction.AgentSettingsUpdated, "AgentSettings", Guid.Empty,
            $"SettingsId={row.Id}; Enabled={row.Enabled}; Model={row.Model}; PreloadConfig={row.PreloadConfig}; " +
            $"DailyMessageCap={row.DailyMessageCap}; HourlyMessageCap={row.HourlyMessageCap}; " +
            $"DailyTokenCap={row.DailyTokenCap}; RetentionDays={row.RetentionDays}", actorUserId);
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
