namespace Humans.Agent.Contracts;

/// <summary>
/// Deletes agent conversations past the configured retention window and records the run for
/// the admin status panel. Implemented by the section, called by
/// <c>AgentConversationRetentionJob</c> (<c>Jobs/</c>). One method returning the deleted
/// count keeps the whole retention rule inside the section and the contract one interface
/// wide.
/// </summary>
public interface IAgentConversationRetention
{
    /// <summary>Purges conversations older than the retention window; returns how many went.</summary>
    Task<int> PurgeExpiredConversationsAsync(CancellationToken cancellationToken);
}
