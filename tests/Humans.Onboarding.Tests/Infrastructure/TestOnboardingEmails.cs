using System.Globalization;
using Humans.Base.Configuration;
using Humans.Onboarding.Services;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Humans.Onboarding.Tests.Infrastructure;

/// <summary>
/// A real <see cref="OnboardingEmails"/> over a stub localizer — the builder is sealed and
/// has no interface, so callers assert on the <c>EmailMessage</c> it produces rather than on
/// a mock of it. Unknown keys render as <c>key#culture</c>, which keeps the recipient's
/// culture observable in the subject; keys in <see cref="Formats"/> render their format
/// string so substitution can be asserted.
/// </summary>
internal static class TestOnboardingEmails
{
    public const string AdminAddress = "admin@humans.example";

    private static readonly Dictionary<string, string> Formats = new(StringComparer.Ordinal)
    {
        ["Onboarding_Email_SignupRejected_Body"] = "<p>Hi {0}</p>{1}<p>{2}</p>",
        ["Onboarding_Email_ReasonLine"] = "<p><strong>Reason:</strong> {0}</p>",
    };

    public static OnboardingEmails Create()
    {
        var localizer = Substitute.For<IStringLocalizer<OnboardingResource>>();
        localizer[Arg.Any<string>()].Returns(call =>
        {
            var key = call.Arg<string>();
            return new LocalizedString(key,
                Formats.GetValueOrDefault(key, $"{key}#{CultureInfo.CurrentUICulture.Name}"));
        });

        return new OnboardingEmails(
            Options.Create(new EmailSettings { AdminAddress = AdminAddress }),
            localizer,
            NullLogger<OnboardingEmails>.Instance);
    }
}
