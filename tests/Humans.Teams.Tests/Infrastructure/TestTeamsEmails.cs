using System.Globalization;
using Humans.Base.Configuration;
using Humans.Teams.Services;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Humans.Teams.Tests.Infrastructure;

/// <summary>
/// A real <see cref="TeamsEmails"/> over a stub localizer — the builder is sealed and
/// has no interface, so callers assert on the <c>EmailMessage</c> it produces rather than on
/// a mock of it. Unknown keys render as <c>key#culture</c>, which keeps the recipient's
/// culture observable in the subject; keys in <see cref="Formats"/> render their format
/// string so substitution can be asserted.
/// </summary>
internal static class TestTeamsEmails
{
    public const string BaseUrl = "https://humans.example";

    private static readonly Dictionary<string, string> Formats = new(StringComparer.Ordinal)
    {
        ["Teams_Email_AddedToTeam_Body"] = "<p>Hi {0}</p><h2>{1}</h2>{2}<a href=\"{3}\">Team</a>",
        ["Teams_Email_ResourcesSection"] = "<p>Resources:</p><ul>{0}</ul>",
    };

    public static TeamsEmails Create()
    {
        var localizer = Substitute.For<IStringLocalizer<TeamsResource>>();
        localizer[Arg.Any<string>()].Returns(call =>
        {
            var key = call.Arg<string>();
            return new LocalizedString(key,
                Formats.GetValueOrDefault(key, $"{key}#{CultureInfo.CurrentUICulture.Name}"));
        });

        return new TeamsEmails(
            Options.Create(new EmailSettings { BaseUrl = BaseUrl }),
            localizer,
            NullLogger<TeamsEmails>.Instance);
    }
}
