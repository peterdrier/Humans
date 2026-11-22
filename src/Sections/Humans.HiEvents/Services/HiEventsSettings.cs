namespace Humans.HiEvents.Services;

/// <summary>
/// <para>
/// <c>ApiUrl</c> ends in <c>/api</c>; <c>AppUrl</c> is the Hi.Events web app, for admin links to an order, and defaults
/// to <c>ApiUrl</c> without <c>/api</c> (the all-in-one image serves both).
/// </para>
/// </summary>
internal sealed class HiEventsSettings
{
    public const string SectionName = "HiEvents";

    public string ApiUrl { get; set; } = string.Empty;
    public string AppUrl { get; set; } = string.Empty;

    /// <summary>From <c>HIEVENTS_API_KEY</c>, never appsettings.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>From <c>HIEVENTS_WEBHOOK_SECRET</c>, never appsettings.</summary>
    public string WebhookSecret { get; set; } = string.Empty;

    /// <summary>From <c>STRIPE_HIEVENTS_KEY</c>: a read-only key on the Stripe account Hi.Events charges into.</summary>
    public string StripeKey { get; set; } = string.Empty;

    /// <summary>Both URLs must be absolute http(s), so a typo stops checkout before any order is made in Hi.Events.</summary>
    public bool IsConfigured => ApiBaseUri is not null && AppBaseUri is not null && !string.IsNullOrEmpty(ApiKey);

    /// <summary>Null when <c>ApiUrl</c> is unset or not an absolute http(s) address.</summary>
    public Uri? ApiBaseUri => HttpUri(ApiUrl.TrimEnd('/') + "/");

    /// <summary><c>AppUrl</c>, or <c>ApiUrl</c> without <c>/api</c>; null when that is not an absolute http(s) address.</summary>
    public Uri? AppBaseUri => HttpUri((!string.IsNullOrEmpty(AppUrl)
        ? AppUrl.TrimEnd('/')
        : ApiUrl.TrimEnd('/') is var api && api.EndsWith("/api", StringComparison.OrdinalIgnoreCase) ? api[..^4] : api) + "/");

    private static Uri? HttpUri(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) || string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal))
            ? uri
            : null;
}

