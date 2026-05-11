using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;

namespace Humans.Application.Interfaces;

public interface IAgentSettingsService : IApplicationService
{
    AgentSettingsInfo Current { get; }
    Task LoadAsync(CancellationToken cancellationToken);
    Task UpdateAsync(Action<AgentSettings> mutator, CancellationToken cancellationToken);
}

public sealed record AgentSettingsInfo(
    int Id,
    bool Enabled,
    string Model,
    AgentPreloadConfig PreloadConfig,
    int DailyMessageCap,
    int HourlyMessageCap,
    int DailyTokenCap,
    int RetentionDays,
    Instant UpdatedAt);
