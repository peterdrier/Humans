using System.Runtime.InteropServices;
using NodaTime;

namespace Humans.Application.Interfaces.Stores;

[StructLayout(LayoutKind.Auto)]
public readonly record struct AgentRateLimitSnapshot(int MessagesToday, int TokensToday, int MessagesThisHour);

public interface IAgentRateLimitStore
{
    /// <summary>Returns daily totals plus the message count in the (Day, Hour) bucket for the given local hour-of-day.</summary>
    AgentRateLimitSnapshot Get(Guid userId, LocalDate day, int hour);

    /// <summary>Increments both the daily totals and the (Day, Hour) message bucket.</summary>
    void Record(Guid userId, LocalDate day, int hour, int messagesDelta, int tokensDelta);
}
