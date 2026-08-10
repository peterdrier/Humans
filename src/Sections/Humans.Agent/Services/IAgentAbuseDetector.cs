namespace Humans.Agent.Services;

internal interface IAgentAbuseDetector
{
    bool IsFlagged(string message, out string? reason);
}
