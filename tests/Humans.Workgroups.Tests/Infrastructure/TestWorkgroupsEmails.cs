using System.Globalization;
using Humans.Base.Configuration;
using Humans.Workgroups.Services;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Humans.Workgroups.Tests.Infrastructure;

/// <summary>
/// Builders for <see cref="WorkgroupsEmails"/>: the class is sealed and has no interface,
/// so callers assert on the <c>EmailMessage</c> it produces rather than on a mock of it.
/// <see cref="Create"/> uses a stub localizer where unknown keys render as
/// <c>key#culture</c>, keeping the recipient's culture observable; <see cref="CreateReal"/>
/// binds the section's real resx set, which is what the culture sweep needs.
/// </summary>
internal static class TestWorkgroupsEmails
{
    public const string BaseUrl = "https://humans.example";

    private static readonly Dictionary<string, string> Formats = new(StringComparer.Ordinal)
    {
        ["Workgroups_Email_WorkgroupNotice_Refused_Body"] = "<p>{0}</p><p>{1}</p>{2}<a href=\"{3}\">Open</a>",
        ["Workgroups_Email_ReasonLine"] = "<p>Reasons: {0}</p>",
    };

    public static WorkgroupsEmails Create()
    {
        var localizer = Substitute.For<IStringLocalizer<WorkgroupsResource>>();
        localizer[Arg.Any<string>()].Returns(call =>
        {
            var key = call.Arg<string>();
            return new LocalizedString(key,
                Formats.GetValueOrDefault(key, $"{key}#{CultureInfo.CurrentUICulture.Name}"));
        });

        return New(localizer);
    }

    public static WorkgroupsEmails CreateReal()
    {
        var factory = new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance);
        return New(new StringLocalizer<WorkgroupsResource>(factory));
    }

    private static WorkgroupsEmails New(IStringLocalizer<WorkgroupsResource> localizer) => new(
        Options.Create(new EmailSettings { BaseUrl = BaseUrl }),
        localizer,
        NullLogger<WorkgroupsEmails>.Instance);
}
