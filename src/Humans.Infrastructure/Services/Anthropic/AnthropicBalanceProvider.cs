using Humans.Application.Configuration;
using Humans.Application.Interfaces;
using Humans.Application.Models;
using Microsoft.Extensions.Options;

namespace Humans.Infrastructure.Services.Anthropic;

/// <summary>
/// Reads the Anthropic billing balance via the admin API key. Never throws —
/// returns BalanceUsd=null + a reason string on every failure path.
/// </summary>
public sealed class AnthropicBalanceProvider(IOptions<AnthropicOptions> options) : IAgentAnthropicBalanceProvider
{
    private readonly AnthropicOptions _options = options.Value;

    public async Task<AgentBalanceStatus> GetBalanceAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.AdminApiKey))
        {
            return new AgentBalanceStatus(BalanceUsd: null, UnavailableReason: "Admin API key not configured");
        }

        // Anthropic has no balance endpoint as of May 2026 (only spend reports). See #709.
        await Task.CompletedTask.ConfigureAwait(false);
        return new AgentBalanceStatus(
            BalanceUsd: null,
            UnavailableReason: "Anthropic does not expose a balance endpoint");
    }
}
