using Humans.Application.Interfaces.Mailer.Dtos;

namespace Humans.Application.Interfaces.Mailer;

/// <summary>
/// Typed read-only MailerLite client surface. No write methods exist by
/// design — outbound is a separate slice with its own review. Pinned by
/// <c>MailerArchitectureTests.IMailerLiteService_HasNoWriteMethods</c>.
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
}
