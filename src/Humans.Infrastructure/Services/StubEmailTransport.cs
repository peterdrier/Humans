using Humans.Application.Interfaces;
using Microsoft.Extensions.Logging;

namespace Humans.Infrastructure.Services;

public class StubEmailTransport : IEmailTransport
{
    private readonly ILogger<StubEmailTransport> _logger;

    public StubEmailTransport(ILogger<StubEmailTransport> logger)
    {
        _logger = logger;
    }

    public Task SendAsync(string recipientEmail, string? recipientName,
        string subject, string htmlBody, string? plainTextBody,
        string? replyTo = null,
        IDictionary<string, string>? extraHeaders = null,
        CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[STUB] Email to {Recipient}: {Subject}", recipientEmail, subject);
        return Task.CompletedTask;
    }
}
