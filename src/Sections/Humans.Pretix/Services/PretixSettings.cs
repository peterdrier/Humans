namespace Humans.Pretix.Services;

/// <summary>
/// One pretix organizer. The event slug comes per order from Tickets (<c>CheckoutRequest.BackendEventRef</c>), and
/// each event's carrier items are found by their internal names, so one set of settings serves every event.
/// </summary>
internal sealed class PretixSettings
{
    public const string SectionName = "Pretix";

    /// <summary>The instance root, e.g. <c>https://pretix.nobodies.team/</c>.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    public string Organizer { get; set; } = string.Empty;

    /// <summary>A team API token; from <c>PRETIX_API_TOKEN</c>.</summary>
    public string ApiToken { get; set; } = string.Empty;

    /// <summary>The <c>secret</c> query value of the webhook URL configured in pretix; from <c>PRETIX_WEBHOOK_SECRET</c>.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    public bool IsConfigured =>
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out _)
        && !string.IsNullOrWhiteSpace(Organizer)
        && !string.IsNullOrWhiteSpace(ApiToken);
}
