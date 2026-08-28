namespace Humans.Agent.Models;

internal sealed class AgentAskRequest
{
    // Server-side cap; the widget's maxlength=2000 is client-only and bypassable.
    public const int MaxMessageLength = 4000;

    public Guid? ConversationId { get; set; }
    public string Message { get; set; } = string.Empty;
}
