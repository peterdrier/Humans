using System.Globalization;
using Humans.Base.Configuration;
using Humans.Users.Services;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Humans.Users.Tests.Infrastructure;

/// <summary>
/// A real <see cref="UsersEmails"/> over a stub localizer — the builder is sealed and has
/// no interface, so callers assert on the <c>EmailMessage</c> it produces rather than on a
/// mock of it. Unknown keys render as <c>key#culture</c>, which keeps the recipient's
/// culture observable in the subject; keys in <see cref="Formats"/> render their format
/// string so substitution can be asserted.
/// </summary>
internal static class TestUsersEmails
{
    public const string BaseUrl = "https://humans.example";

    private static readonly Dictionary<string, string> Formats = new(StringComparer.Ordinal)
    {
        ["Users_Email_EmailVerification_Body"] = "<p>Hi {0} ({1})</p><a href=\"{2}\">Verify</a>",
        ["Users_Email_AccountDeletionRequested_Body"] = "<p>Hi {0}</p><p>{1}</p><a href=\"{2}\">Account</a>",
        ["Users_Email_AccessSuspended_Body"] = "<p>Hi {0}</p><p>{1}</p><a href=\"{2}\">Account</a><p>{3}</p>",
    };

    public static UsersEmails Create()
    {
        var localizer = Substitute.For<IStringLocalizer<UsersResource>>();
        localizer[Arg.Any<string>()].Returns(call =>
        {
            var key = call.Arg<string>();
            return new LocalizedString(key,
                Formats.GetValueOrDefault(key, $"{key}#{CultureInfo.CurrentUICulture.Name}"));
        });

        return new UsersEmails(
            Options.Create(new EmailSettings { BaseUrl = BaseUrl, AdminAddress = "admin@example.com" }),
            localizer,
            NullLogger<UsersEmails>.Instance);
    }
}
