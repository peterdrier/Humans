namespace Humans.Email.Services;

internal sealed class StubEmailTransport(ILogger<StubEmailTransport> logger) : IEmailTransport
{
    public Task SendAsync(string recipientEmail, string? recipientName,
        string subject, string htmlBody, string? plainTextBody,
        string? replyTo = null,
        IDictionary<string, string>? extraHeaders = null,
        CancellationToken cancellationToken = default,
        bool redact = false, string? templateName = null)
    {
        if (redact)
            logger.LogInformation("[STUB] Email: {TemplateName}", templateName);
        else
            logger.LogInformation("[STUB] Email to {Recipient}: {Subject}", recipientEmail, subject);
        return Task.CompletedTask;
    }
}
