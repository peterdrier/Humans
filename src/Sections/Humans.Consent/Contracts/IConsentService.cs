using Humans.Base.Interfaces;
using NodaTime;

namespace Humans.Consent.Contracts;

public interface IConsentService : IApplicationService
{
    Task<IReadOnlySet<Guid>> GetConsentedVersionIdsAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>Every input user id appears, with an empty set when it has no consents.</summary>
    Task<IReadOnlyDictionary<Guid, IReadOnlySet<Guid>>> GetConsentMapForUsersAsync(
        IReadOnlyList<Guid> userIds, CancellationToken ct = default);

    /// <summary>One row per active, required document scoped to <paramref name="teamId"/>. No side effects.</summary>
    Task<IReadOnlyList<RequiredConsentRow>> GetRequiredConsentRowsForUserAsync(
        Guid userId, Guid teamId, CancellationToken ct = default);

    /// <summary>Documents whose current version the user hasn't signed.</summary>
    Task<IReadOnlyList<string>> GetPendingDocumentNamesAsync(Guid userId, CancellationToken ct = default);

    /// <summary>True when the user has signed the current version of every document the team requires.</summary>
    Task<bool> HasAllRequiredConsentsForTeamAsync(Guid userId, Guid teamId, CancellationToken ct = default);

    /// <summary>The subset of <paramref name="userIds"/> with all of the team's required consents, in one batch.</summary>
    Task<IReadOnlySet<Guid>> GetUsersWithAllRequiredConsentsForTeamAsync(
        IEnumerable<Guid> userIds, Guid teamId, CancellationToken ct = default);

    /// <summary>
    /// Counts the documents required by Volunteers, by the user's teams, and by Coordinators when they
    /// coordinate a user-created team.
    /// </summary>
    Task<ConsentProgress> GetConsentProgressAsync(Guid userId, CancellationToken ct = default);

    /// <summary>
    /// Users with an active role assignment who are missing a Volunteers consent whose grace period has expired.
    /// A positive <paramref name="expiringWithin"/> also includes deadlines within it, for advance reminders.
    /// </summary>
    Task<IReadOnlyList<Guid>> GetUsersPastConsentDeadlineAsync(
        Duration? expiringWithin = null, CancellationToken ct = default);

    Task<int> GetConsentRecordCountAsync(
        Guid userId, CancellationToken ct = default);

    /// <summary>Returns null when the version or its canonical Spanish content is unavailable.</summary>
    Task<ConsentReviewDetail?> GetConsentReviewDetailAsync(
        Guid documentVersionId, Guid userId, CancellationToken ct = default);

    /// <summary>
    /// <see cref="ConsentReviewInfo.None"/> when nothing is recorded. A merged-away id reads its survivor's review.
    /// </summary>
    Task<ConsentReviewInfo> GetConsentReviewAsync(Guid userId, CancellationToken ct = default);

    /// <summary>A member missing from the map reads as <see cref="ConsentReviewInfo.None"/>.</summary>
    Task<IReadOnlyDictionary<Guid, ConsentReviewInfo>> GetConsentReviewsAsync(CancellationToken ct = default);
}
