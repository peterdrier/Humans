namespace Humans.Agent.Models;

internal sealed record AgentTurnRequest(Guid ConversationId, Guid UserId, string Message, string Locale);
