using Humans.Base.Demo;
using Microsoft.Extensions.Options;

namespace Humans.MailerLite.Services;

/// <summary>MailerLite's audience sync does not skip demo people, so a configured key would push them to the vendor.</summary>
internal sealed class MailerLiteDemoSafetyCheck(IOptions<MailerLiteOptions> options) : IDemoSafetyCheck
{
    public Task<string?> RefusalAsync(CancellationToken ct) =>
        Task.FromResult(string.IsNullOrWhiteSpace(options.Value.ApiKey)
            ? null
            : "MailerLite is configured, and its audience sync would send demo people to MailerLite.");
}
