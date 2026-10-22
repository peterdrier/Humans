using Humans.Calendar.Data;
using Humans.AuditLog.Contracts;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Calendar.Services;

internal sealed class CalendarFeedTokenService(ICalendarRepository repo, IAuditLogService audit)
    : ICalendarFeedTokenService, IUserDataContributor, IUserMerge
{
    public Task<Guid?> GetAsync(Guid userId, CancellationToken ct = default) =>
        repo.GetFeedTokenAsync(userId, ct);

    // One call, not read-then-write: two racing creates would otherwise 500 on the primary key.
    public async Task<Guid> EnsureAsync(Guid userId, CancellationToken ct = default)
    {
        var (token, created) = await repo.GetOrAddFeedTokenAsync(userId, Guid.NewGuid(), ct);
        if (created)
            await audit.LogAsync(AuditAction.CalendarFeedTokenCreated, "User", userId,
                "Personal calendar feed token created", userId);
        return token;
    }

    public async Task<Guid> RotateAsync(Guid userId, CancellationToken ct = default)
    {
        var token = Guid.NewGuid();
        await repo.SetFeedTokenAsync(userId, token, ct);
        await audit.LogAsync(AuditAction.CalendarFeedTokenRotated, "User", userId,
            "Personal calendar feed token rotated", userId);
        return token;
    }

    /// <summary>
    /// Reports whether a feed exists, never the token: it is a live credential, and export files get forwarded.
    /// </summary>
    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        [new UserDataSlice(
            CalendarFeedToken,
            new { HasFeed = await GetAsync(userId, ct) is not null })];

    internal const string CalendarFeedToken = "CalendarFeedToken";

    private static readonly IReadOnlyDictionary<string, string?> Erasure =
        new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            [CalendarFeedToken] = null
        };

    public IReadOnlyDictionary<string, string?> ErasureDeclaration => Erasure;

    public Task EraseForUserAsync(Guid userId, CancellationToken ct) =>
        repo.DeleteFeedTokenAsync(userId, ct);

    /// <summary>
    /// Deletes the merged-away token rather than moving it: moving it would swap the survivor's live URL for the
    /// dead account's.
    /// </summary>
    public Task ReassignAsync(
        Guid mergedFromUserId,
        Guid mergedToUserId,
        Guid actorUserId,
        Instant now,
        CancellationToken ct) =>
        repo.DeleteFeedTokenAsync(mergedFromUserId, ct);
}
