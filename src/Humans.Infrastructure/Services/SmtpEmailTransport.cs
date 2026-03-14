using Humans.Application.Interfaces;
using Humans.Infrastructure.Configuration;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Humans.Infrastructure.Services;

/// <summary>
/// SMTP transport implementation using MailKit.
/// Handles only connection and send — no rendering, no template wrapping, no metrics.
/// </summary>
public class SmtpEmailTransport : IEmailTransport
{
    private readonly EmailSettings _settings;
    private readonly ILogger<SmtpEmailTransport> _logger;

    public SmtpEmailTransport(IOptions<EmailSettings> settings, ILogger<SmtpEmailTransport> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task SendAsync(
        string recipientEmail,
        string? recipientName,
        string subject,
        string htmlBody,
        string? plainTextBody,
        string? replyTo = null,
        IDictionary<string, string>? extraHeaders = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_settings.FromName, _settings.FromAddress));
            message.To.Add(recipientName is not null
                ? new MailboxAddress(recipientName, recipientEmail)
                : MailboxAddress.Parse(recipientEmail));
            message.Subject = subject;

            if (!string.IsNullOrEmpty(replyTo))
            {
                message.ReplyTo.Add(MailboxAddress.Parse(replyTo));
            }

            if (extraHeaders is not null)
            {
                foreach (var (name, value) in extraHeaders)
                {
                    message.Headers.Add(name, value);
                }
            }

            var bodyBuilder = new BodyBuilder
            {
                HtmlBody = htmlBody
            };
            if (!string.IsNullOrEmpty(plainTextBody))
            {
                bodyBuilder.TextBody = plainTextBody;
            }
            message.Body = bodyBuilder.ToMessageBody();

            using var client = new SmtpClient();

            await client.ConnectAsync(
                _settings.SmtpHost,
                _settings.SmtpPort,
                _settings.UseSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.None,
                cancellationToken);

            if (!string.IsNullOrEmpty(_settings.Username))
            {
                await client.AuthenticateAsync(_settings.Username, _settings.Password, cancellationToken);
            }

            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);

            _logger.LogInformation("Email sent to {Recipient}: {Subject}", recipientEmail, subject);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {Recipient}: {Subject}", recipientEmail, subject);
            throw;
        }
    }
}
