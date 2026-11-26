namespace Humans.Agent.Domain;

/// <summary>
/// Stored in <see cref="AgentMessage.Outcome"/> for a turn that did not end in an answer. Null means answered.
/// </summary>
internal static class AgentTurnOutcome
{
    public const string LimitReached = "limit_reached";
    public const string DistressReply = "distress_reply";
    public const string Failed = "failed";
}
