using System.Globalization;
using Humans.Feedback.Services;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.Feedback.Tests.Infrastructure;

/// <summary>
/// A real <see cref="FeedbackEmails"/> over a stub localizer — the builder is sealed and
/// has no interface, so callers assert on the <c>EmailMessage</c> it produces rather than on
/// a mock of it. Unknown keys render as <c>key#culture</c>, which keeps the recipient's
/// culture observable in the subject; keys in <see cref="Formats"/> render their format
/// string so substitution can be asserted.
/// </summary>
internal static class TestFeedbackEmails
{
    private static readonly Dictionary<string, string> Formats = new(StringComparer.Ordinal)
    {
        ["Feedback_Email_FeedbackResponse_Body"] =
            "<p>Hi {0},</p><blockquote>{1}</blockquote>{2}",
    };

    public static FeedbackEmails Create()
    {
        var localizer = Substitute.For<IStringLocalizer<FeedbackResource>>();
        localizer[Arg.Any<string>()].Returns(call =>
        {
            var key = call.Arg<string>();
            return new LocalizedString(key,
                Formats.GetValueOrDefault(key, $"{key}#{CultureInfo.CurrentUICulture.Name}"));
        });

        return new FeedbackEmails(localizer, NullLogger<FeedbackEmails>.Instance);
    }
}
