namespace Humans.Infrastructure.Configuration;

/// <summary>
/// Configuration for SMTP email service.
/// </summary>
public class EmailSettings
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "Email";

    /// <summary>
    /// SMTP server hostname.
    /// </summary>
    public string SmtpHost { get; set; } = "smtp-relay.gmail.com";

    /// <summary>
    /// SMTP server port.
    /// </summary>
    public int SmtpPort { get; set; } = 587;

    /// <summary>
    /// Whether to use SSL/TLS.
    /// </summary>
    public bool UseSsl { get; set; } = true;

    /// <summary>
    /// SMTP username (usually email address).
    /// </summary>
    public string Username { get; set; } = string.Empty;

    /// <summary>
    /// SMTP password or app-specific password.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// From email address.
    /// </summary>
    public string FromAddress { get; set; } = "noreply@nobodies.team";

    /// <summary>
    /// From display name.
    /// </summary>
    public string FromName { get; set; } = "Nobodies Collective";

    /// <summary>
    /// Admin email address for notifications.
    /// </summary>
    public string AdminAddress { get; set; } = "admin@nobodies.team";

    /// <summary>
    /// Base URL for links in emails.
    /// </summary>
    public string BaseUrl { get; set; } = "https://humans.nobodies.team";

    /// <summary>
    /// Days before suspension to start sending re-consent reminders.
    /// Production: 30, QA: 3.
    /// </summary>
    public int ConsentReminderDaysBeforeSuspension { get; set; } = 30;

    /// <summary>
    /// Minimum days between re-consent reminders for the same user.
    /// Production: 7, QA: 1.
    /// </summary>
    public int ConsentReminderCooldownDays { get; set; } = 7;
}
