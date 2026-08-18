using Humans.Agent.Models;
using Humans.Base.Interfaces;

namespace Humans.Agent.Services.Anthropic;

/// <summary>
/// Optional adapter for the Anthropic admin/billing API. When the admin key
/// is not configured (or the endpoint is unreachable), implementations must
/// return a value with <c>BalanceUsd = null</c> and a short
/// <c>UnavailableReason</c>; they MUST NOT throw. Spec §709 acceptance:
/// "balance unavailable" must degrade gracefully to a console link.
/// </summary>
internal interface IAgentAnthropicBalanceProvider : IApplicationService
{
    Task<AgentBalanceStatus> GetBalanceAsync(CancellationToken cancellationToken);
}
