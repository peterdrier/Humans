namespace Humans.Email.Services;

internal interface IEmailTransport
{
    // Redacted sends must log only template/type, never recipient, subject or exception contents.
    Task SendAsync(string recipientEmail, string? recipientName,
        string subject, string htmlBody, string? plainTextBody,
        string? replyTo = null,
        IDictionary<string, string>? extraHeaders = null,
        CancellationToken cancellationToken = default,
        bool redact = false, string? templateName = null);
}
