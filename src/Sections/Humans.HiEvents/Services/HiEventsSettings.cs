namespace Humans.HiEvents.Services;

/// <summary>
/// <c>ApiUrl</c> ends in <c>/api</c>. The product ids are the Hi.Events products a ticket line and a donation line are
/// sold as; neither may carry taxes or fees in Hi.Events, or the order total stops matching the lines and checkout
/// creation is refused.
/// </summary>
internal sealed class HiEventsSettings
{
    public const string SectionName = "HiEvents";

    public string ApiUrl { get; set; } = string.Empty;
    public int TicketProductId { get; set; }
    public int DonationProductId { get; set; }

    /// <summary>From <c>HIEVENTS_API_KEY</c>, never appsettings.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>From <c>HIEVENTS_WEBHOOK_SECRET</c>, never appsettings.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrEmpty(ApiUrl) && !string.IsNullOrEmpty(ApiKey) && TicketProductId > 0;
}
