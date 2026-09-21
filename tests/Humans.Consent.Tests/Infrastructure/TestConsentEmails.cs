using System.Globalization;
using Humans.Base.Configuration;
using Humans.Consent.Services;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Humans.Consent.Tests.Infrastructure;

/// <summary>
/// A real <see cref="ConsentEmails"/> over a stub localizer — the builder is sealed and has
/// no interface, so callers assert on the <c>EmailMessage</c> it produces rather than on a
/// mock of it. Unknown keys render as <c>key#culture</c>, which keeps the recipient's
/// culture observable in the subject; keys in <see cref="Formats"/> render their format
/// string so substitution can be asserted.
/// </summary>
internal static class TestConsentEmails
{
    public const string BaseUrl = "https://humans.example";

    private static readonly Dictionary<string, string> Formats = new(StringComparer.Ordinal)
    {
        ["Consent_Email_ReConsentRequired_Subject_Single"] = "Please re-accept: {0}",
        ["Consent_Email_ReConsentsRequired_Body"] = "<p>Hi {0}</p><ul>{1}</ul><a href=\"{2}/Consent\">Review</a>",
        ["Consent_Email_ReConsentReminder_Subject"] = "{0} days left",
        ["Consent_Email_ReConsentReminder_Body"] = "<p>Hi {0}</p><p>{1} days</p><ul>{2}</ul><a href=\"{3}/Consent\">Review</a>",
    };

    public static ConsentEmails Create()
    {
        var localizer = Substitute.For<IStringLocalizer<ConsentResource>>();
        localizer[Arg.Any<string>()].Returns(call =>
        {
            var key = call.Arg<string>();
            return new LocalizedString(key,
                Formats.GetValueOrDefault(key, $"{key}#{CultureInfo.CurrentUICulture.Name}"));
        });

        return new ConsentEmails(
            Options.Create(new EmailSettings { BaseUrl = BaseUrl }),
            localizer,
            NullLogger<ConsentEmails>.Instance);
    }
}
