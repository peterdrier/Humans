namespace Humans.HiEvents.Services;

/// <summary>
/// <para>
/// <c>ApiUrl</c> ends in <c>/api</c>.
/// </para>
/// <para>
/// <c>Events</c> is keyed by the Hi.Events event id (a Humans event's <c>BackendEventRef</c>), so several events can sell
/// at once. Neither product may carry taxes or fees in Hi.Events, or the order total stops matching the lines and
/// checkout creation is refused.
/// </para>
/// </summary>
internal sealed class HiEventsSettings
{
    public const string SectionName = "HiEvents";

    public string ApiUrl { get; set; } = string.Empty;
    public Dictionary<string, HiEventsEventProducts> Events { get; set; } = new(StringComparer.Ordinal);

    /// <summary>From <c>HIEVENTS_API_KEY</c>, never appsettings.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>From <c>HIEVENTS_WEBHOOK_SECRET</c>, never appsettings.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    public bool IsConfigured => !string.IsNullOrEmpty(ApiUrl) && !string.IsNullOrEmpty(ApiKey);
}

/// <summary>The Hi.Events products one event sells a ticket line and a donation line as; 0 means none.</summary>
internal sealed class HiEventsEventProducts
{
    public int TicketProductId { get; set; }
    public int DonationProductId { get; set; }
}
