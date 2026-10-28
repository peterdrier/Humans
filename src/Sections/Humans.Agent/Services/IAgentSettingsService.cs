using NodaTime;
using Humans.Agent.Domain;
using Humans.Base.Interfaces;

namespace Humans.Agent.Services;

internal interface IAgentSettingsService : IApplicationService
{
    AgentSettingsDto Current { get; }
    Task LoadAsync(CancellationToken cancellationToken);
    Task UpdateAsync(Action<AgentSettings> mutator, Guid actorUserId, CancellationToken cancellationToken);

    /// <summary>
    /// The models the admin may pick, newest first, always including the saved one. Never throws: if Anthropic can't
    /// be reached the list is just the saved model, with <see cref="AgentModelOptions.LiveListAvailable"/> false.
    /// </summary>
    Task<AgentModelOptions> GetModelOptionsAsync(CancellationToken cancellationToken);
}

internal sealed record AgentModelOptions(IReadOnlyList<AgentModelOption> Models, bool LiveListAvailable);

/// <summary><paramref name="Price"/> is null when the pricing table doesn't know the model yet.</summary>
internal sealed record AgentModelOption(string Id, string DisplayName, AgentPricing.PriceRow? Price);

internal sealed record AgentSettingsDto(
    bool Enabled,
    string Model,
    AgentPreloadConfig PreloadConfig,
    int DailyMessageCap,
    int HourlyMessageCap,
    int DailyTokenCap,
    int RetentionDays,
    Instant UpdatedAt);
