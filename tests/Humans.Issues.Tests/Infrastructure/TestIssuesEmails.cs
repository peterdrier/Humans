using System.Globalization;
using Humans.Base.Configuration;
using Humans.Issues.Services;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Humans.Issues.Tests.Infrastructure;

/// <summary>
/// A real <see cref="IssuesEmails"/> over a stub localizer — the builder is sealed and
/// has no interface, so callers assert on the <c>EmailMessage</c> it produces rather than on
/// a mock of it. Unknown keys render as <c>key#culture</c>, which keeps the recipient's
/// culture observable in the subject; keys in <see cref="Formats"/> render their format
/// string so substitution can be asserted.
/// </summary>
internal static class TestIssuesEmails
{
    public const string BaseUrl = "https://humans.example";

    private static readonly Dictionary<string, string> Formats = new(StringComparer.Ordinal)
    {
        ["Issues_Email_IssueComment_Subject"] = "New comment on {0}",
        ["Issues_Email_IssueComment_Body"] =
            "<p>Hi {0},</p><h2>{1}</h2>{2}<p><a href=\"{3}\">Open the issue</a></p>",
    };

    public static IssuesEmails Create()
    {
        var localizer = Substitute.For<IStringLocalizer<IssuesResource>>();
        localizer[Arg.Any<string>()].Returns(call =>
        {
            var key = call.Arg<string>();
            return new LocalizedString(key,
                Formats.GetValueOrDefault(key, $"{key}#{CultureInfo.CurrentUICulture.Name}"));
        });

        return new IssuesEmails(
            Options.Create(new EmailSettings { BaseUrl = BaseUrl }),
            localizer,
            NullLogger<IssuesEmails>.Instance);
    }
}
