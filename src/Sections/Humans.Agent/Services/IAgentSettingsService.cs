using NodaTime;
using Humans.Agent.Domain;
using Humans.Base.Interfaces;

namespace Humans.Agent.Services;

internal interface IAgentSettingsService : IApplicationService
{
    AgentSettingsDto Current { get; }
    Task LoadAsync(CancellationToken cancellationToken);
    Task UpdateAsync(Action<AgentSettings> mutator, CancellationToken cancellationToken);
}

internal sealed record AgentSettingsDto(
    bool Enabled,
    string Model,
    AgentPreloadConfig PreloadConfig,
    int DailyMessageCap,
    int HourlyMessageCap,
    int DailyTokenCap,
    int RetentionDays,
    Instant UpdatedAt);
