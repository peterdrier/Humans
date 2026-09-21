using System.Globalization;
using Humans.Auth.Services;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Auth.Tests.Infrastructure;

/// <summary>
/// A real <see cref="AuthEmails"/> over a stub localizer — the builder is sealed and has no
/// interface, so callers assert on the <c>EmailMessage</c> it produces rather than on a mock
/// of it. Unknown keys render as <c>key#culture</c>, which keeps the recipient's culture
/// observable in the subject; keys in <see cref="Formats"/> render their format string so
/// substitution can be asserted.
/// </summary>
internal static class TestAuthEmails
{
    private static readonly Dictionary<string, string> Formats = new(StringComparer.Ordinal)
    {
        ["Auth_Email_MagicLinkLogin_Body"] = "<p>Hi {0}</p><a href=\"{1}\">Sign in</a>",
        ["Auth_Email_MagicLinkSignup_Body"] = "<a href=\"{0}\">Create your account</a>",
    };

    public static AuthEmails Create()
    {
        var localizer = Substitute.For<IStringLocalizer<AuthResource>>();
        localizer[Arg.Any<string>()].Returns(call =>
        {
            var key = call.Arg<string>();
            return new LocalizedString(key,
                Formats.GetValueOrDefault(key, $"{key}#{CultureInfo.CurrentUICulture.Name}"));
        });

        return new AuthEmails(localizer, NullLogger<AuthEmails>.Instance);
    }
}
