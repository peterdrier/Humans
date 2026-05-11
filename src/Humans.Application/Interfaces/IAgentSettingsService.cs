using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces;

public interface IAgentSettingsService : IApplicationService
{
    AgentSettingsDto Current { get; }
    Task LoadAsync(CancellationToken cancellationToken);
    Task UpdateAsync(Action<AgentSettings> mutator, CancellationToken cancellationToken);
}

public sealed record AgentSettingsDto(
    bool Enabled,
    string Model,
    AgentPreloadConfig PreloadConfig,
    int DailyMessageCap,
    int HourlyMessageCap,
    int DailyTokenCap,
    int RetentionDays,
    Instant UpdatedAt);
