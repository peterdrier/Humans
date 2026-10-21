using System.Globalization;
using Humans.Base.Configuration;
using Humans.Grants.Services;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Humans.Grants.Tests.Infrastructure;

/// <summary>
/// Builders for <see cref="GrantsEmails"/>: sealed and interface-less, so callers assert
/// on the <c>EmailMessage</c> it produces. <see cref="Create"/> renders unknown keys as
/// <c>key#culture</c>, keeping the recipient's culture observable; <see cref="CreateReal"/>
/// binds the section's real resx set.
/// </summary>
internal static class TestGrantsEmails
{
    public const string BaseUrl = "https://humans.example";

    public static GrantsEmails Create()
    {
        var localizer = Substitute.For<IStringLocalizer<GrantsResource>>();
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

    public static GrantsEmails CreateReal()
    {
        var factory = new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance);
        return New(new StringLocalizer<GrantsResource>(factory));
    }

    private static GrantsEmails New(IStringLocalizer<GrantsResource> localizer) => new(
        Options.Create(new EmailSettings { BaseUrl = BaseUrl }),
        localizer,
        NullLogger<GrantsEmails>.Instance);
}
