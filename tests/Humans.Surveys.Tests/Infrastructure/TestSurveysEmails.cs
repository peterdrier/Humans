using System.Globalization;
using Humans.Base.Configuration;
using Humans.Surveys.Services;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Humans.Surveys.Tests.Infrastructure;

/// <summary>
/// A real <see cref="SurveysEmails"/> over a stub localizer — the builder is sealed and has
/// no interface, so callers assert on the <c>EmailMessage</c> it produces rather than on a
/// mock of it. Unknown keys render as <c>key#culture</c>, which keeps the recipient's
/// culture observable in the subject; keys in <see cref="Formats"/> render their format
/// string so substitution can be asserted.
/// </summary>
internal static class TestSurveysEmails
{
    public const string BaseUrl = "https://humans.example";

    private static readonly Dictionary<string, string> Formats = new(StringComparer.Ordinal)
    {
        ["Surveys_Email_SurveyInvitation_Subject"] = "Please complete: {0}",
        ["Surveys_Email_SurveyInvitation_DefaultMessage"] = "You're invited to complete <strong>{0}</strong>.",
        ["Surveys_Email_SurveyInvitation_Body"] =
            "<h2>{1}</h2><p>Hi {0},</p>{3}<p><a href=\"{2}\">Open the survey</a></p>",
        ["Surveys_Email_SurveyReminder_Subject"] = "Reminder: {0}",
        ["Surveys_Email_SurveyReminder_Body"] =
            "<p>Hi {0},</p><h2>{1}</h2><p><a href=\"{2}\">Open the survey</a></p>",
    };

    public static SurveysEmails Create()
    {
        var localizer = Substitute.For<IStringLocalizer<SurveysResource>>();
        localizer[Arg.Any<string>()].Returns(call =>
        {
            var key = call.Arg<string>();
            return new LocalizedString(key,
                Formats.GetValueOrDefault(key, $"{key}#{CultureInfo.CurrentUICulture.Name}"));
        });

        return new SurveysEmails(
            Options.Create(new EmailSettings { BaseUrl = BaseUrl }),
            localizer,
            NullLogger<SurveysEmails>.Instance);
    }
}
