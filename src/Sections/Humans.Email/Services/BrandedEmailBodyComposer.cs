using Humans.Base.Helpers;
using Humans.Base.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Humans.Email.Services;

internal sealed class BrandedEmailBodyComposer(IOptions<EmailSettings> settings, IHostEnvironment hostEnvironment)
    : IEmailBodyComposer
{
    private readonly string _baseUrl = settings.Value.BaseUrl;
    private readonly string _environmentName = hostEnvironment.EnvironmentName;

    public (string HtmlBody, string PlainTextBody) Compose(string htmlContent, string? unsubscribeUrl = null, string? culture = null)
    {
        var plainText = HtmlPlainTextConverter.Convert(htmlContent);
        if (unsubscribeUrl is not null)
        {
            var footer = $"{BrandedEmailTemplate.GetUnsubscribeLabel(culture)} ({unsubscribeUrl})";
            plainText = plainText.Length == 0 ? footer : $"{plainText}\n\n{footer}";
        }

        return (BrandedEmailTemplate.Wrap(htmlContent, _baseUrl, _environmentName, unsubscribeUrl, culture), plainText);
    }
}
