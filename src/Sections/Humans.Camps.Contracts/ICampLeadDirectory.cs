namespace Humans.Camps.Contracts;

/// <summary>
/// The camp-lead reads Base needs. Carved out at the section's G5 because
/// <c>SystemTeamSyncJob</c> injected <c>ICampRepository</c> directly and drove these two
/// queries itself — a job reaching past the service layer into another section's repository
/// (design §15 step 6b). The repository is internal to the section now, so the job consumes
/// this instead and the "who is a camp lead" rule lives beside the rows it reads.
/// </summary>
public interface ICampLeadDirectory
{
    /// <summary>User ids holding an active camp-lead role in the public year's season.</summary>
    Task<IReadOnlyList<Guid>> GetActiveLeadUserIdsAsync(CancellationToken cancellationToken = default);

    /// <summary>Whether the user is an active lead of any camp in the public year's season.</summary>
    Task<bool> IsLeadAnywhereAsync(Guid userId, CancellationToken cancellationToken = default);
}
