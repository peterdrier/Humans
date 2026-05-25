using Humans.Application.Interfaces.Email;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Services;

public class StubEmailTransport(ILogger<StubEmailTransport> logger) : IEmailTransport
{
    public Task SendAsync(string recipientEmail, string? recipientName,
        string subject, string htmlBody, string? plainTextBody,
        string? replyTo = null,
        IDictionary<string, string>? extraHeaders = null,
        CancellationToken cancellationToken = default)
    {
        logger.LogInformation("[STUB] Email to {Recipient}: {Subject}", recipientEmail, subject);
        return Task.CompletedTask;
    }
}
