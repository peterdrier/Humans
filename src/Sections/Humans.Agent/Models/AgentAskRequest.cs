namespace Humans.Agent.Models;

internal sealed class AgentAskRequest
{
    public Guid? ConversationId { get; set; }
    public string Message { get; set; } = string.Empty;
}
