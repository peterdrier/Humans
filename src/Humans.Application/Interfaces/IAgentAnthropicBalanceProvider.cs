using Humans.Application.Models;

namespace Humans.Application.Interfaces;

/// <summary>
/// Optional adapter for the Anthropic admin/billing API. When the admin key
/// is not configured (or the endpoint is unreachable), implementations must
/// return a value with <c>BalanceUsd = null</c> and a short
/// <c>UnavailableReason</c>; they MUST NOT throw. Spec §709 acceptance:
/// "balance unavailable" must degrade gracefully to a console link.
/// </summary>
public interface IAgentAnthropicBalanceProvider : IApplicationService
{
    Task<AgentBalanceStatus> GetBalanceAsync(CancellationToken cancellationToken);
}
