using Humans.Calendar.Data;
using Humans.Gdpr.Contracts;
using Humans.Users.Contracts;
using NodaTime;

namespace Humans.Calendar.Services;

/// <summary>
/// <see cref="ICalendarFeedTokenService"/> over <c>calendar_feed_tokens</c>, plus this
/// section's seat at the two user-lifecycle fan-outs the table obliges it to join.
/// </summary>
/// <remarks>
/// Not folded into <c>CalendarService</c>: that interface is the event mutation surface
/// behind <c>CachingCalendarService</c>'s occurrence snapshot, and a per-member
/// credential has nothing to cache there. Registered Singleton like the repository it
/// wraps.
/// </remarks>
internal sealed class CalendarFeedTokenService(ICalendarRepository repo)
    : ICalendarFeedTokenService, IUserDataContributor, IUserMerge
{
    public Task<Guid?> GetAsync(Guid userId, CancellationToken ct = default) =>
        repo.GetFeedTokenAsync(userId, ct);

    // One call, not a read then a write: the null check has to happen where the insert
    // does, or two first-time views of /Calendar race and one of them 500s on the
    // primary key. The loser gets the winner's token, which is the same working feed.
    public Task<Guid> EnsureAsync(Guid userId, CancellationToken ct = default) =>
        repo.GetOrAddFeedTokenAsync(userId, Guid.NewGuid(), ct);

    public async Task<Guid> RotateAsync(Guid userId, CancellationToken ct = default)
    {
        var token = Guid.NewGuid();
        await repo.SetFeedTokenAsync(userId, token, ct);
        return token;
    }

    /// <summary>
    /// Reports whether a feed exists, never the token itself. That a member subscribed
    /// is their personal data and belongs in the export; the secret is a live credential,
    /// and an export file is a document people forward. They can read the URL off
    /// <c>/Calendar</c>, which is the one place it is shown.
    /// </summary>
    public async Task<IReadOnlyList<UserDataSlice>> ContributeForUserAsync(Guid userId, CancellationToken ct) =>
        [new UserDataSlice(
            CalendarFeedToken,
            new { HasFeed = await GetAsync(userId, ct) is not null })];

    /// <summary>
    /// Export key: whether the member has a personal iCal feed. The token itself is never
    /// exported; it is a live credential and an export file gets forwarded.
    /// </summary>
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
    /// Merge deletes the eliminated account's token rather than re-FKing it: the
    /// survivor has their own, one member cannot hold two, and moving the row would
    /// silently swap the survivor's live URL for the tombstone's. This is what Users
    /// did inline before the table moved here.
    /// </summary>
    public Task ReassignAsync(
        Guid mergedFromUserId,
        Guid mergedToUserId,
        Guid actorUserId,
        Instant now,
        CancellationToken ct) =>
        repo.DeleteFeedTokenAsync(mergedFromUserId, ct);
}
