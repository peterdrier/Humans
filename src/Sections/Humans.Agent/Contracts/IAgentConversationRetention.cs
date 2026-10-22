namespace Humans.Agent.Contracts;

public interface IAgentConversationRetention
{
    /// <summary>Returns how many conversations were deleted.</summary>
    Task<int> PurgeExpiredConversationsAsync(CancellationToken cancellationToken);
}
