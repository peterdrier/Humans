using Humans.Infrastructure.Helpers;
using Humans.Email.Contracts;
using Humans.Infrastructure.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Humans.Email.Services;

/// <summary>
/// Infrastructure-layer implementation of <see cref="IEmailBodyComposer"/>.
/// Owns the <see cref="EmailSettings.BaseUrl"/> and
/// <see cref="IHostEnvironment.EnvironmentName"/> inputs so the
/// Application-layer <c>OutboxEmailService</c> does not have to take them.
/// Delegates the actual wrapping to
/// <see cref="BrandedEmailTemplate"/> + <see cref="HtmlPlainTextConverter"/>.
/// </summary>
internal sealed class BrandedEmailBodyComposer(IOptions<EmailSettings> settings, IHostEnvironment hostEnvironment)
    : IEmailBodyComposer
{
    private readonly string _baseUrl = settings.Value.BaseUrl;
    private readonly string _environmentName = hostEnvironment.EnvironmentName;

    public (string HtmlBody, string PlainTextBody) Compose(string htmlContent, string? unsubscribeUrl = null) => (
        BrandedEmailTemplate.Wrap(htmlContent, _baseUrl, _environmentName, unsubscribeUrl),
        HtmlPlainTextConverter.Convert(htmlContent));
}
