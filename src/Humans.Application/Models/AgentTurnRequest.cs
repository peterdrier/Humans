namespace Humans.Application.Models;

public sealed record AgentTurnRequest(Guid ConversationId, Guid UserId, string Message, string Locale);
