namespace Humans.Email.Services;

internal interface IEmailBodyComposer
{
    /// <summary>
    /// Returns the branded HTML and a plain-text body derived from it. <paramref name="unsubscribeUrl"/>, when set,
    /// goes in the footer.
    /// </summary>
    (string HtmlBody, string PlainTextBody) Compose(string htmlContent, string? unsubscribeUrl = null, string? culture = null);
}
