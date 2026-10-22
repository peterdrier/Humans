namespace Humans.Users.Contracts;

/// <summary>
/// Every <see cref="UserInfo"/> accessor resolves a merged-away id forward to the surviving account, so no caller
/// follows a chain. <see cref="UserInfo.MergedUserIds"/> is the one place archived ids surface, for callers that
/// union rows by them.
/// </summary>
public interface IUserService
{
    /// <summary>
    /// A merged-away id returns the survivor's record, so <c>.Id</c> may differ from <paramref name="userId"/>. A
    /// GDPR-erased id returns its own <see cref="UserState.Deleted"/> row. Null only when no row exists.
    /// </summary>
    ValueTask<UserInfo?> GetUserInfoAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// One entry per living human: merge tombstones and erased rows are omitted. A new collection per call, so
    /// callers can iterate without locking.
    /// </summary>
    Task<IReadOnlyCollection<UserInfo>> GetAllUserInfosAsync(CancellationToken ct = default);

    /// <summary>
    /// Keyed by the requested id, valued by the resolved record: all three ids of an A→B→C chain map to C. Ids with
    /// no user are absent.
    /// </summary>
    ValueTask<IReadOnlyDictionary<Guid, UserInfo>> GetUserInfosAsync(
        IReadOnlyCollection<Guid> userIds,
        CancellationToken ct = default);

    /// <summary>
    /// Unordered: callers sort and take at the presentation layer. Rejected and profileless users are never
    /// matched, nor emergency contacts. Services are auth-free, so passing <see cref="PersonSearchFields.Admin"/>
    /// or <see cref="PersonSearchFields.LegalName"/> from a non-admin endpoint is a bug only review catches.
    /// </summary>
    Task<IReadOnlyList<HumanSearchResult>> SearchUsersAsync(
        string query,
        PersonSearchFields fields,
        int limit = 10,
        CancellationToken ct = default);

    /// <summary>
    /// No merge redirect: a tombstone comes back as itself. Only for Users' own merge, deletion and admin code and
    /// the claims transformation, which must stamp a tombstone's <see cref="UserState.Merged"/>. Null when no row exists.
    /// </summary>
    ValueTask<UserInfo?> GetRawUserInfoAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Tombstones included. Cheap to filter by id: it is the warmed snapshot.</summary>
    Task<IReadOnlyCollection<UserInfo>> GetAllRawUserInfosAsync(CancellationToken ct = default);

    /// <summary>Verified addresses only, with the gmail/googlemail twin. Null if no match.</summary>
    Task<UserInfo?> GetByEmailOrAlternateAsync(string email, CancellationToken ct = default);
}
