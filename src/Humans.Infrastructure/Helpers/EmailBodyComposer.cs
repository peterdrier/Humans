namespace Humans.Infrastructure.Helpers;

public static class EmailBodyComposer
{
    public static (string HtmlBody, string PlainTextBody) Compose(
        string htmlContent,
        string baseUrl,
        string environmentName,
        string? unsubscribeUrl = null)
    {
        return (
            BrandedEmailTemplate.Wrap(htmlContent, baseUrl, environmentName, unsubscribeUrl),
            HtmlPlainTextConverter.Convert(htmlContent));
    }
}
