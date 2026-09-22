namespace Humans.Email.Services;

/// <summary>
/// Composes an outbound email body from a rendered HTML content fragment.
/// Returns both the branded HTML wrapper (footer, base URL, environment
/// banner, optional unsubscribe link) and a derived plain-text body. The
/// implementation captures the environment-derived values (base URL, env name)
/// so <c>OutboxEmailService</c> stays free of <c>IHostEnvironment</c> and
/// configuration dependencies.
/// </summary>
internal interface IEmailBodyComposer
{
    /// <summary>
    /// Wraps <paramref name="htmlContent"/> in the branded template and
    /// returns the paired HTML/plain-text bodies. <paramref name="unsubscribeUrl"/>
    /// is rendered in both the HTML footer (when non-null) and, for
    /// opt-outable categories, will be communicated as List-Unsubscribe by
    /// the transport layer.
    /// </summary>
    (string HtmlBody, string PlainTextBody) Compose(string htmlContent, string? unsubscribeUrl = null);
}
