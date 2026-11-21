namespace Humans.Pretix.Services;

/// <summary>
/// One pretix organizer. The event slug comes per order from Tickets (<c>CheckoutRequest.BackendEventRef</c>), so one
/// set of settings serves every event on the instance. The catalogue items are plain carriers for Humans' prices: one
/// admission item taxed like a ticket, one non-admission item with no tax for donations, both "free price".
/// </summary>
internal sealed class PretixSettings
{
    public const string SectionName = "Pretix";

    /// <summary>The instance root, e.g. <c>https://pretix.nobodies.team/</c>.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    public string Organizer { get; set; } = string.Empty;

    public int TicketItemId { get; set; }

    public int DonationItemId { get; set; }

    /// <summary>A team API token; from <c>PRETIX_API_TOKEN</c>.</summary>
    public string ApiToken { get; set; } = string.Empty;

    /// <summary>The last path segment of the webhook URL configured in pretix; from <c>PRETIX_WEBHOOK_SECRET</c>.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    public bool IsConfigured =>
        Uri.TryCreate(BaseUrl, UriKind.Absolute, out _)
        && !string.IsNullOrWhiteSpace(Organizer)
        && !string.IsNullOrWhiteSpace(ApiToken)
        && TicketItemId > 0
        && DonationItemId > 0;
}
