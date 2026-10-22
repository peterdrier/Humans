using Humans.Base.Attributes;
using Humans.Base.Interfaces;
using Humans.MailerLite.Services.Dtos;
using NodaTime;

namespace Humans.MailerLite.Services;

/// <summary>
/// Cached in memory, populated on first read and refreshed only by <see cref="RefreshAsync"/>, so page loads don't
/// burn the MailerLite rate limit.
/// </summary>
internal interface IMailerLiteService : IApplicationService
{
    Task<MailerLiteAccountSummary> GetAccountSummaryAsync(
        CancellationToken ct = default);

    Task<IReadOnlyList<MailerLiteGroup>> ListGroupsAsync(
        CancellationToken ct = default);

    IAsyncEnumerable<MailerLiteSubscriber> ListSubscribersAsync(
        CancellationToken ct = default);

    Instant? LastFetchedAt { get; }

    Task RefreshAsync(CancellationToken ct = default);

    /// <summary>Throws <see cref="InvalidOperationException"/> unless <paramref name="name"/> starts with "Humans - ".</summary>
    [ExternalWrite]
    Task<MailerLiteGroup> CreateGroupAsync(string name, CancellationToken ct = default);

    /// <summary>Throws <see cref="InvalidOperationException"/> unless the group's name starts with "Humans - ".</summary>
    [ExternalWrite]
    Task AssignSubscriberToGroupAsync(string subscriberId, string groupId, CancellationToken ct = default);

    /// <summary>Same prefix guard as assign.</summary>
    [ExternalWrite]
    Task UnassignSubscriberFromGroupAsync(string subscriberId, string groupId, CancellationToken ct = default);

    /// <summary>
    /// One POST per email, then assignment to the group. Partial failure is possible: <see cref="BulkImportResult.Errors"/>
    /// counts failed emails, and the rest are still assigned. Same prefix guard as assign.
    /// </summary>
    [ExternalWrite]
    Task<BulkImportResult> BulkImportSubscribersToGroupAsync(
        string groupId, IReadOnlyList<string> emails, CancellationToken ct = default);

    /// <summary>
    /// GDPR erasure, so not limited to "Humans - " groups. A 404 counts as success, keeping the retried erasure
    /// cascade idempotent.
    /// </summary>
    [ExternalWrite]
    Task DeleteSubscriberAsync(string email, CancellationToken ct = default);
}
