namespace Humans.GoogleIntegration.Contracts;

/// <summary>
/// Keys are group emails; values are user ids only. <see cref="IGoogleGroupSync"/> hydrates and filters users for
/// every source in one pass, so a source must not call <c>IUserService</c>. Two sources claiming one group is a
/// collision: the group is skipped and audited.
/// </summary>
public interface IGoogleGroupMembershipSource
{
    /// <summary>A non-null <paramref name="groupKey"/> returns at most that entry, or nothing when unclaimed.</summary>
    Task<Dictionary<string, Guid[]>> GetExpectedAsync(
        string? groupKey = null,
        CancellationToken ct = default);
}
