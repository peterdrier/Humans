using System.Globalization;
using Humans.Base.Configuration;
using Humans.Preapprovals.Services;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Humans.Preapprovals.Tests.Infrastructure;

/// <summary>
/// Builders for <see cref="PreapprovalsEmails"/>: sealed and interface-less, so callers assert
/// on the <c>EmailMessage</c> it produces. <see cref="Create"/> renders unknown keys as
/// <c>key#culture</c>, keeping the recipient's culture observable; <see cref="CreateReal"/>
/// binds the section's real resx set.
/// </summary>
internal static class TestPreapprovalsEmails
{
    public const string BaseUrl = "https://humans.example";

    public static PreapprovalsEmails Create()
    {
        var localizer = Substitute.For<IStringLocalizer<PreapprovalsResource>>();
        localizer[Arg.Any<string>()].Returns(call =>
        {
            var key = call.Arg<string>();
            return new LocalizedString(key, $"{key}#{CultureInfo.CurrentUICulture.Name}");
        });
        localizer[Arg.Any<string>(), Arg.Any<object[]>()].Returns(call =>
        {
            var key = call.Arg<string>();
            var args = call.Arg<object[]>();
            return new LocalizedString(key, $"{key}#{CultureInfo.CurrentUICulture.Name}:{string.Join("|", args)}");
        });
        return New(localizer);
    }

    public static PreapprovalsEmails CreateReal()
    {
        var factory = new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance);
        return New(new StringLocalizer<PreapprovalsResource>(factory));
    }

    private static PreapprovalsEmails New(IStringLocalizer<PreapprovalsResource> localizer) => new(
        Options.Create(new EmailSettings { BaseUrl = BaseUrl }),
        localizer,
        NullLogger<PreapprovalsEmails>.Instance);
}
