using Humans.Application.Configuration;
using Humans.Application.Interfaces;
using Humans.Application.Models;
using Microsoft.Extensions.Options;

namespace Humans.Infrastructure.Services.Anthropic;

/// <summary>
/// Optional reader for the Anthropic billing balance. Requires the separate
/// admin API key (<c>Anthropic__AdminApiKey</c>) — the runtime workspace key
/// (<c>Anthropic__ApiKey</c>) cannot read org billing. When the admin key
/// isn't configured we short-circuit to "unavailable"; the panel shows a
/// link to the Anthropic Console in that case. The provider never throws
/// — every failure path emits <c>BalanceUsd = null</c> with a one-line
/// reason so the admin view can render without try/catch on the caller.
/// </summary>
public sealed class AnthropicBalanceProvider : IAgentAnthropicBalanceProvider
{
    private readonly AnthropicOptions _options;

    public AnthropicBalanceProvider(IOptions<AnthropicOptions> options)
    {
        _options = options.Value;
    }

    public async Task<AgentBalanceStatus> GetBalanceAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.AdminApiKey))
        {
            return new AgentBalanceStatus(BalanceUsd: null, UnavailableReason: "Admin API key not configured");
        }

        // The Anthropic public docs (May 2026) describe the Admin API but do
        // not publish a stable "current balance" endpoint — the closest is
        // /v1/organizations/usage_report and /v1/organizations/cost_report,
        // which return spend, not credit. Until Anthropic ships a balance
        // endpoint we degrade gracefully: surface the unavailable reason and
        // the admin panel links to the console. Spec #709 explicitly accepts
        // this fallback over fake numbers. When the endpoint ships, restore
        // the HttpClient + try/catch wiring and call it here.
        await Task.CompletedTask.ConfigureAwait(false);
        return new AgentBalanceStatus(
            BalanceUsd: null,
            UnavailableReason: "Anthropic does not expose a balance endpoint");
    }
}
