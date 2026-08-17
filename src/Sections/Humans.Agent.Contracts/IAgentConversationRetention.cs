namespace Humans.Agent.Contracts;

/// <summary>
/// Deletes agent conversations past the configured retention window and records the run for
/// the admin status panel. Implemented by the section and called by
/// <c>AgentConversationRetentionJob</c>, which moved from <c>Humans.Infrastructure/Jobs</c>
/// into <c>Humans.Agent/Contracts/</c> at G5 lane 5b-5 (nobodies-collective/Humans#866), so
/// both halves are now this section's.
/// </summary>
/// <remarks>
/// The job used to take <c>IAgentRepository</c>, <c>IAgentSettingsService</c> and
/// <c>IAgentRetentionRunStore</c> and orchestrate across them — a Base caller reaching past a
/// section's service layer into its repository. One method returning the deleted count keeps
/// the whole retention rule inside the section and the leaf one interface wide.
/// </remarks>
public interface IAgentConversationRetention
{
    /// <summary>Purges conversations older than the retention window; returns how many went.</summary>
    Task<int> PurgeExpiredConversationsAsync(CancellationToken cancellationToken);
}
