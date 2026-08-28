namespace Humans.Camps.Contracts;

/// <summary>
/// The camp-lead reads Base needs. <c>ICampRepository</c> is section-internal, so
/// <c>SystemTeamSyncJob</c> consumes this instead and the "who is a camp lead" rule
/// lives beside the rows it reads.
/// </summary>
public interface ICampLeadDirectory
{
    /// <summary>User ids holding an active camp-lead role in any camp, any season
    /// (year-agnostic — the Barrio Leads team spans years).</summary>
    Task<IReadOnlyList<Guid>> GetActiveLeadUserIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether the user holds an active camp-lead role in any camp, any season.</summary>
    Task<bool> IsLeadAnywhereAsync(Guid userId, CancellationToken cancellationToken = default);
}
