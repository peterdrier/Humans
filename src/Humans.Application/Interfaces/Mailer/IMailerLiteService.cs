using Humans.Application.Interfaces.Mailer.Dtos;
using NodaTime;

namespace Humans.Application.Interfaces.Mailer;

/// <summary>
/// MailerLite client surface. Reads cover account summary, groups, and
/// subscribers. Writes are narrow: limited to creating "Humans - "-prefixed
/// groups and managing membership in those groups. Pinned by
/// <c>MailerArchitectureTests.IMailerLiteService_OnlyAllowsAudienceWrites</c>.
///
/// Implementations cache subscribers, groups, and the derived account
/// summary in memory so page loads (e.g. /Mailer/Admin) don't burn the
/// MailerLite rate limit on every request. The cache populates lazily on
/// first read and refreshes only via <see cref="RefreshAsync"/>.
/// </summary>
public interface IMailerLiteService : IApplicationService
{
    Task<MailerLiteAccountSummary> GetAccountSummaryAsync(
        CancellationToken ct = default);

    Task<IReadOnlyList<MailerLiteGroup>> ListGroupsAsync(
        CancellationToken ct = default);

    IAsyncEnumerable<MailerLiteSubscriber> ListSubscribersAsync(
        CancellationToken ct = default);

    Task<MailerLiteSubscriber?> GetSubscriberAsync(
        string email, CancellationToken ct = default);

    Instant? LastFetchedAt { get; }

    Task RefreshAsync(CancellationToken ct = default);

    /// <summary>
    /// Creates a new group in MailerLite. Runtime-rejects with
    /// <see cref="InvalidOperationException"/> if <paramref name="name"/> does
    /// not start with <c>"Humans - "</c>.
    /// </summary>
    Task<MailerLiteGroup> CreateGroupAsync(string name, CancellationToken ct = default);

    /// <summary>
    /// Assigns an existing subscriber to a group. Runtime-rejects with
    /// <see cref="InvalidOperationException"/> if the target group's
    /// <see cref="MailerLiteGroup.Name"/> does not start with <c>"Humans - "</c>.
    /// </summary>
    Task AssignSubscriberToGroupAsync(string subscriberId, string groupId, CancellationToken ct = default);

    /// <summary>
    /// Removes a subscriber from a group. Same prefix guard as assign.
    /// </summary>
    Task UnassignSubscriberFromGroupAsync(string subscriberId, string groupId, CancellationToken ct = default);

    /// <summary>
    /// Creates-or-updates each email as a MailerLite subscriber (via individual
    /// POST /api/subscribers calls) and assigns them to the target group.
    /// Partial failure is possible: <see cref="BulkImportResult.Errors"/> counts
    /// per-email failures; successfully processed emails are still assigned.
    /// Same prefix guard as assign.
    /// </summary>
    Task<BulkImportResult> BulkImportSubscribersToGroupAsync(
        string groupId, IReadOnlyList<string> emails, CancellationToken ct = default);
}
