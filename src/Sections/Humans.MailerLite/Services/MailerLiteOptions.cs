namespace Humans.MailerLite.Services;

internal sealed class MailerLiteOptions
{
    public const string SectionName = "MailerLite";
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://connect.mailerlite.com";
    public string ApiVersion { get; set; } = "2038-01-01";
}
