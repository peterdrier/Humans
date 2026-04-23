using Humans.Application.Interfaces.Email;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Helpers;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Infrastructure-layer implementation of <see cref="IEmailBodyComposer"/>.
/// Owns the <see cref="EmailSettings.BaseUrl"/> and
/// <see cref="IHostEnvironment.EnvironmentName"/> inputs so the
/// Application-layer <c>OutboxEmailService</c> does not have to take them.
/// Delegates the actual wrapping to
/// <see cref="BrandedEmailTemplate"/> + <see cref="HtmlPlainTextConverter"/>.
/// </summary>
public sealed class BrandedEmailBodyComposer : IEmailBodyComposer
{
    private readonly string _baseUrl;
    private readonly string _environmentName;

    public BrandedEmailBodyComposer(IOptions<EmailSettings> settings, IHostEnvironment hostEnvironment)
    {
        _baseUrl = settings.Value.BaseUrl;
        _environmentName = hostEnvironment.EnvironmentName;
    }

    public (string HtmlBody, string PlainTextBody) Compose(string htmlContent, string? unsubscribeUrl = null)
    {
        return (
            BrandedEmailTemplate.Wrap(htmlContent, _baseUrl, _environmentName, unsubscribeUrl),
            HtmlPlainTextConverter.Convert(htmlContent));
    }
}
