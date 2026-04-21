using System.Runtime.InteropServices;
using NodaTime;

namespace Humans.Application.Interfaces.Stores;

[StructLayout(LayoutKind.Auto)]
public readonly record struct AgentRateLimitSnapshot(int MessagesToday, int TokensToday);

public interface IAgentRateLimitStore
{
    AgentRateLimitSnapshot Get(Guid userId, LocalDate day);
    void Record(Guid userId, LocalDate day, int messagesDelta, int tokensDelta);
}
