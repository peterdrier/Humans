using System.Runtime.InteropServices;
using NodaTime;

namespace Humans.Agent.Services.Stores;

[StructLayout(LayoutKind.Auto)]
internal readonly record struct AgentRateLimitSnapshot(int MessagesToday, int TokensToday, int MessagesThisHour)
{
    public bool IsAtLimit(AgentSettingsDto settings) =>
        MessagesToday >= settings.DailyMessageCap ||
        TokensToday >= settings.DailyTokenCap ||
        MessagesThisHour >= settings.HourlyMessageCap;
}

internal interface IAgentRateLimitStore
{
    /// <summary>Returns daily totals plus the message count in the (Day, Hour) bucket for the given local hour-of-day.</summary>
    AgentRateLimitSnapshot Get(Guid userId, LocalDate day, int hour);

    /// <summary>Increments both the daily totals and the (Day, Hour) message bucket.</summary>
    void Record(Guid userId, LocalDate day, int hour, int messagesDelta, int tokensDelta);
}
