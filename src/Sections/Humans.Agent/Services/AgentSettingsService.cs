using Humans.Agent.Contracts;
using Humans.AuditLog.Contracts;
using Humans.Agent.Data;
using Humans.Agent.Services.Anthropic;
using Humans.Agent.Services.Stores;
using NodaTime;
using Humans.Agent.Domain;

namespace Humans.Agent.Services;

internal sealed class AgentSettingsService(
    IAgentRepository repo,
    IAgentSettingsStore store,
    IClock clock,
    IAuditLogService audit,
    IAnthropicClient anthropic,
    ILogger<AgentSettingsService> logger)
    : IAgentSettingsService, IAgentAvailability
{
    // The list is fetched on every settings-tab render, so a slow Anthropic must not hold the page up.
    private static readonly TimeSpan ModelListTimeout = TimeSpan.FromSeconds(5);

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
        await audit.LogAsync(AgentAuditActions.AgentSettingsUpdated, "AgentSettings", Guid.Empty,
            $"SettingsId={row.Id}; Enabled={row.Enabled}; Model={row.Model}; PreloadConfig={row.PreloadConfig}; " +
            $"DailyMessageCap={row.DailyMessageCap}; HourlyMessageCap={row.HourlyMessageCap}; " +
            $"DailyTokenCap={row.DailyTokenCap}; RetentionDays={row.RetentionDays}", actorUserId);
    }

    public async Task<AgentModelOptions> GetModelOptionsAsync(CancellationToken cancellationToken)
    {
        var saved = store.Current.Model;
        var live = await GetLiveModelsAsync(cancellationToken);
        var models = (live ?? [])
            .Select(m => new AgentModelOption(m.Id, m.DisplayName, AgentPricing.FindPriceRow(m.Id)))
            .ToList();
        if (!models.Any(m => string.Equals(m.Id, saved, StringComparison.Ordinal)))
            models.Insert(0, new AgentModelOption(saved, saved, AgentPricing.FindPriceRow(saved)));
        return new AgentModelOptions(models, live is not null);
    }

    private async Task<IReadOnlyList<AnthropicModelInfo>?> GetLiveModelsAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ModelListTimeout);
        try
        {
            return await anthropic.ListModelsAsync(timeout.Token);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Could not list Anthropic models; the settings dropdown shows only the saved model.");
            return null;
        }
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
