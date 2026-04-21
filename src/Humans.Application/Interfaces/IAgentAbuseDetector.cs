namespace Humans.Application.Interfaces;

public interface IAgentAbuseDetector
{
    bool IsFlagged(string message, out string? reason);
}
