
namespace Humans.Agent.Services.Anthropic;

internal sealed class AnthropicOptions
{
    public const string SectionName = "Anthropic";

    /// <summary>User-secrets locally, env var <c>Anthropic__ApiKey</c> in Coolify.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Used only by the balance lookup. Unset renders "balance unavailable" with a Console link, never an error.
    /// </summary>
    public string AdminApiKey { get; set; } = string.Empty;

    /// <summary>Model id sent to the API when <c>AgentSettings.Model</c> is empty.</summary>
    public string DefaultModel { get; set; } = "claude-sonnet-4-6";

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Enforced server-side regardless of model behavior.</summary>
    public int MaxToolCallsPerTurn { get; set; } = 3;
}
