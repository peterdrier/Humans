using Humans.Agent.Models;
using Humans.Base.Interfaces;

namespace Humans.Agent.Services.Anthropic;

/// <summary>
/// Implementations MUST NOT throw: unconfigured or unreachable returns <c>BalanceUsd = null</c> and a short
/// <c>UnavailableReason</c>, which the page degrades to a Console link.
/// </summary>
internal interface IAgentAnthropicBalanceProvider : IApplicationService
{
    Task<AgentBalanceStatus> GetBalanceAsync(CancellationToken cancellationToken);
}
