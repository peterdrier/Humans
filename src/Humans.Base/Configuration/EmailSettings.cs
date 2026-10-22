namespace Humans.Base.Configuration;

public class EmailSettings
{
    public const string SectionName = "Email";

    public string SmtpHost { get; set; } = "smtp-relay.gmail.com";

    public int SmtpPort { get; set; } = 587;

    public bool UseSsl { get; set; } = true;

    public string Username { get; set; } = string.Empty;

    /// <summary>SMTP password or app-specific password.</summary>
    public string Password { get; set; } = string.Empty;

    public string FromAddress { get; set; } = "humans@nobodies.team";

    public string FromName { get; set; } = "Nobodies Collective";

    /// <summary>Admin address for notifications.</summary>
    public string AdminAddress { get; set; } = "admin@nobodies.team";

    public string BaseUrl { get; set; } = "https://humans.nobodies.team";

    /// <summary>Days before suspension to start re-consent reminders. Production: 30, QA: 3.</summary>
    public int ConsentReminderDaysBeforeSuspension { get; set; } = 30;

    /// <summary>Minimum days between re-consent reminders for one user. Production: 7, QA: 1.</summary>
    public int ConsentReminderCooldownDays { get; set; } = 7;

    public int OutboxBatchSize { get; set; } = 10;

    /// <summary>Send retries before a message is marked failed.</summary>
    public int OutboxMaxRetries { get; set; } = 10;

    /// <summary>Days to retain sent outbox messages before cleanup.</summary>
    public int OutboxRetentionDays { get; set; } = 150;
}
