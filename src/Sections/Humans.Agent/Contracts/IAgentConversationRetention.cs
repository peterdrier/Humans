namespace Humans.Agent.Contracts;

public interface IAgentConversationRetention
{
    /// <summary>
    /// Deletes conversations older than the configured retention window and records the run for the admin
    /// status panel. Returns how many were deleted.
    /// </summary>
    Task<int> PurgeExpiredConversationsAsync(CancellationToken cancellationToken);
}
