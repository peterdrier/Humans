namespace Humans.Infrastructure.Configuration;

public class AnthropicOptions
{
    public const string SectionName = "Anthropic";

    /// <summary>Bearer API key. Read from user-secrets locally and env var <c>Anthropic__ApiKey</c> in Coolify.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>Model id sent to the API when <c>AgentSettings.Model</c> is empty.</summary>
    public string DefaultModel { get; set; } = "claude-sonnet-4-6";

    /// <summary>Request timeout.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Hard cap on tool calls per turn. Enforced server-side regardless of model behavior.</summary>
    public int MaxToolCallsPerTurn { get; set; } = 3;
}
