using Humans.Domain.Enums;

namespace Humans.Application.Models;

/// <summary>
/// Payload emitted when the agent calls <c>route_to_issue</c>. The agent does
/// not create the issue server-side; the client opens the issue submission
/// form pre-filled with these values so the user can review and submit.
/// </summary>
public sealed record AgentIssueProposal(
    string Title,
    IssueCategory Category,
    string Description);
