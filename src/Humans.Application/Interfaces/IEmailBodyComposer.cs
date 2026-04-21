namespace Humans.Application.Interfaces;

/// <summary>
/// Composes an outbound email body from a rendered HTML content fragment.
/// Returns both the branded HTML wrapper (footer, base URL, environment
/// banner, optional unsubscribe link) and a derived plain-text body. The
/// implementation captures environment-derived values (base URL, env name)
/// that belong in Infrastructure, so <c>OutboxEmailService</c> can stay
/// Application-layer and free of <c>IHostEnvironment</c>/configuration
/// dependencies.
/// </summary>
public interface IEmailBodyComposer
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
