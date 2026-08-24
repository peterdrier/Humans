namespace Humans.Agent.Contracts;

/// <summary>
/// Who spoke a message. On the section's public surface because
/// <see cref="AgentMessageSnapshot"/> carries it out to the Backdoor machine API
/// (nobodies-collective/Humans#1128).
/// </summary>
public enum AgentRole
{
    User = 0,
    Assistant = 1,
    Tool = 2
}
