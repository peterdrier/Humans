namespace Humans.Web.Models.Agent;

public sealed class AgentAskRequest
{
    public Guid? ConversationId { get; set; }
    public string Message { get; set; } = string.Empty;
}
