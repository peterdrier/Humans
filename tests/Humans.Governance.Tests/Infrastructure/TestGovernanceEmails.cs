using System.Globalization;
using Humans.Base.Configuration;
using Humans.Governance.Services;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Humans.Governance.Tests.Infrastructure;

/// <summary>
/// A real <see cref="GovernanceEmails"/> over a stub localizer — the builder is sealed and
/// has no interface, so callers assert on the <c>EmailMessage</c> it produces rather than on
/// a mock of it. Unknown keys render as <c>key#culture</c>, which keeps the recipient's
/// culture observable in the subject; keys in <see cref="Formats"/> render their format
/// string so substitution can be asserted.
/// </summary>
internal static class TestGovernanceEmails
{
    public const string BaseUrl = "https://humans.example";

    private static readonly Dictionary<string, string> Formats = new(StringComparer.Ordinal)
    {
        ["Governance_Email_AssemblyVoteOpened_Body"] = "<p>Hi {0}</p><h2>{1}</h2><p>{2}</p><a href=\"{3}\">Vote</a>{4}",
    };

    public static GovernanceEmails Create()
    {
        var localizer = Substitute.For<IStringLocalizer<GovernanceResource>>();
        localizer[Arg.Any<string>()].Returns(call =>
        {
            var key = call.Arg<string>();
            return new LocalizedString(key,
                Formats.GetValueOrDefault(key, $"{key}#{CultureInfo.CurrentUICulture.Name}"));
        });

        return new GovernanceEmails(
            Options.Create(new EmailSettings { BaseUrl = BaseUrl }),
            localizer,
            NullLogger<GovernanceEmails>.Instance);
    }
}
