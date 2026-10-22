using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Humans.Base.Configuration;

namespace Humans.Email.Health;

/// <summary>Connects and authenticates without sending mail.</summary>
internal sealed class SmtpHealthCheck(IOptions<EmailSettings> settings, ILogger<SmtpHealthCheck> logger) : IHealthCheck
{
    private readonly EmailSettings _settings = settings.Value;

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(_settings.SmtpHost))
        {
            return HealthCheckResult.Degraded("SMTP not configured");
        }

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var client = new SmtpClient();

            await client.ConnectAsync(
                _settings.SmtpHost,
                _settings.SmtpPort,
                _settings.UseSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.None,
                cancellationToken);

            if (!string.IsNullOrEmpty(_settings.Username))
            {
                await client.AuthenticateAsync(
                    _settings.Username,
                    _settings.Password,
                    cancellationToken);
            }

            await client.DisconnectAsync(true, cancellationToken);

            return HealthCheckResult.Healthy("SMTP connection and authentication successful");
        }
        catch (AuthenticationException ex)
        {
            logger.LogWarning(ex, "SMTP authentication failed");
            return HealthCheckResult.Unhealthy(
                "SMTP authentication failed - check credentials",
                ex);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "SMTP health check failed");
            return HealthCheckResult.Unhealthy(
                $"SMTP connection failed: {ex.Message}",
                ex);
        }
    }
}
