using Humans.Issues.Contracts;

namespace Humans.Agent.Models;

/// <summary>The issue is never created server-side: the client opens the submission form pre-filled.</summary>
internal sealed record AgentIssueProposal(
    string Title,
    IssueCategory Category,
    string Description);
