namespace Humans.Application.Interfaces.Mailer;

/// <summary>
/// MailerLite client configuration. Bound from <c>MailerLite:*</c> in
/// configuration (user-secrets in dev, env-var-shaped <c>MailerLite__ApiKey</c>
/// or flat <c>MAILERLITE_API_KEY</c> in PR/prod — see Program.cs binding).
/// </summary>
public sealed class MailerLiteOptions
{
    public const string SectionName = "MailerLite";
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://connect.mailerlite.com";
    public string ApiVersion { get; set; } = "2038-01-01";

    /// <summary>
    /// Cron expression for the recurring audience sync job. Empty (default) means
    /// the recurring job is not registered — admins can still trigger syncs on
    /// demand via the /Mailer/Admin "Push Now" button. Set to e.g. <c>"0 6 * * *"</c>
    /// to enable a daily 06:00 UTC run.
    /// </summary>
    public string AudienceSyncCron { get; set; } = string.Empty;

    /// <summary>
    /// Max subscribers per call to <c>POST /api/groups/{id}/subscribers/import</c>.
    /// Implementer to verify ML v2 current ceiling; 50 is the documented safe value
    /// observed in 2026-05.
    /// </summary>
    public int BulkImportChunkSize { get; set; } = 50;
}
