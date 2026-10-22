using Microsoft.Extensions.Options;
using Humans.Agent.Models;

namespace Humans.Agent.Services.Anthropic;

/// <summary>Never throws: every failure returns BalanceUsd=null with a reason.</summary>
internal sealed class AnthropicBalanceProvider(IOptions<AnthropicOptions> options) : IAgentAnthropicBalanceProvider
{
    private readonly AnthropicOptions _options = options.Value;

    public async Task<AgentBalanceStatus> GetBalanceAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.AdminApiKey))
        {
            return new AgentBalanceStatus(BalanceUsd: null, UnavailableReason: "Admin API key not configured");
        }

        // Anthropic has no balance endpoint as of May 2026, only spend reports.
        await Task.CompletedTask.ConfigureAwait(false);
        return new AgentBalanceStatus(
            BalanceUsd: null,
            UnavailableReason: "Anthropic does not expose a balance endpoint");
    }
}
