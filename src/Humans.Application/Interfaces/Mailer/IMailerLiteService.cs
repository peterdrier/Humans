using Humans.Application.Interfaces.Mailer.Dtos;
using NodaTime;

namespace Humans.Application.Interfaces.Mailer;

/// <summary>
/// Typed read-only MailerLite client surface. No write methods exist by
/// design — outbound is a separate slice with its own review. Pinned by
/// <c>MailerArchitectureTests.IMailerLiteService_HasNoWriteMethods</c>.
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
}
