using Humans.Base.Attributes;
using Humans.Base.Interfaces;

namespace Humans.Email.Contracts;

/// <summary>The one send path for outbound email.</summary>
public interface IEmailService : IApplicationService
{
    /// <summary>
    /// Enqueues the message. An opted-out recipient of an opt-outable category is skipped; otherwise the
    /// message gets List-Unsubscribe headers and a footer link. An <see cref="EmailMessage.Urgent"/> message
    /// triggers an immediate drain.
    /// </summary>
    [ExternalWrite]
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
