using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Profiles.Infrastructure.Configuration;

namespace Profiles.Web.Health;

/// <summary>
/// Health check that validates SMTP connectivity and authentication.
/// Connects and authenticates without sending any email.
/// </summary>
public class SmtpHealthCheck : IHealthCheck
{
    private readonly EmailSettings _settings;
    private readonly ILogger<SmtpHealthCheck> _logger;

    public SmtpHealthCheck(
        IOptions<EmailSettings> settings,
        ILogger<SmtpHealthCheck> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        // Skip check if SMTP is not configured
        if (string.IsNullOrEmpty(_settings.SmtpHost))
        {
            return HealthCheckResult.Degraded("SMTP not configured");
        }

        try
        {
            using var client = new SmtpClient();

            // Connect to SMTP server
            await client.ConnectAsync(
                _settings.SmtpHost,
                _settings.SmtpPort,
                _settings.UseSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.None,
                cancellationToken);

            // Authenticate if credentials provided
            if (!string.IsNullOrEmpty(_settings.Username))
            {
                await client.AuthenticateAsync(
                    _settings.Username,
                    _settings.Password,
                    cancellationToken);
            }

            // Disconnect cleanly
            await client.DisconnectAsync(true, cancellationToken);

            return HealthCheckResult.Healthy("SMTP connection and authentication successful");
        }
        catch (AuthenticationException ex)
        {
            _logger.LogWarning(ex, "SMTP authentication failed");
            return HealthCheckResult.Unhealthy(
                "SMTP authentication failed - check credentials",
                ex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "SMTP health check failed");
            return HealthCheckResult.Unhealthy(
                $"SMTP connection failed: {ex.Message}",
                ex);
        }
    }
}
