
namespace Humans.Agent.Services.Anthropic;

internal sealed class AnthropicOptions
{
    public const string SectionName = "Anthropic";

    /// <summary>User-secrets locally, env var <c>Anthropic__ApiKey</c> in Coolify.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Selects the admin balance-unavailable reason; the status card links to the Console.
    /// </summary>
    public string AdminApiKey { get; set; } = string.Empty;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Enforced server-side regardless of model behavior.</summary>
    public int MaxToolCallsPerTurn { get; set; } = 3;
}
