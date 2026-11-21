namespace Humans.HiEvents.Services;

/// <summary>
/// <para>
/// <c>ApiUrl</c> ends in <c>/api</c>; <c>AppUrl</c> is the Hi.Events web app, for admin links to an order, and defaults
/// to <c>ApiUrl</c> without <c>/api</c> (the all-in-one image serves both).
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
    public string AppUrl { get; set; } = string.Empty;
    public Dictionary<string, HiEventsEventProducts> Events { get; set; } = new(StringComparer.Ordinal);

    /// <summary>From <c>HIEVENTS_API_KEY</c>, never appsettings.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>From <c>HIEVENTS_WEBHOOK_SECRET</c>, never appsettings.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    public bool IsConfigured => ApiBaseUri is not null && !string.IsNullOrEmpty(ApiKey);

    /// <summary>Null when <c>ApiUrl</c> is unset or not an absolute http(s) address.</summary>
    public Uri? ApiBaseUri =>
        Uri.TryCreate(ApiUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
        && (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) || string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal))
            ? uri
            : null;

    public string ResolvedAppUrl => !string.IsNullOrEmpty(AppUrl)
        ? AppUrl.TrimEnd('/')
        : ApiUrl.TrimEnd('/') is var api && api.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? api[..^4] : api;
}

/// <summary>The Hi.Events products one event sells a ticket line and a donation line as; 0 means none.</summary>
internal sealed class HiEventsEventProducts
{
    public int TicketProductId { get; set; }
    public int DonationProductId { get; set; }
}
