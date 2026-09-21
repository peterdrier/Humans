using Humans.Email.Contracts;

namespace Humans.Surveys.Services;

/// <summary>
/// Surveys' contribution to the template gallery at <c>/Email/EmailPreview</c>: one sample
/// per template <see cref="SurveysEmails"/> can send, built through the same builder the
/// real send path uses, so the gallery cannot drift from what members receive
/// (peterdrier/Humans#1651). Sample data only — no recipient is real, no token works and
/// nothing is sent.
/// </summary>
internal sealed class SurveysEmailPreviews(SurveysEmails emails) : IEmailPreviewContributor
{
    private const string SampleTitle = "Volunteer availability for the next build";
    private const string SampleToken = "sample-token";

    public IReadOnlyList<EmailPreviewSample> Samples(EmailPreviewPersona persona)
    {
        ArgumentNullException.ThrowIfNull(persona);
        var (culture, name, email) = (persona.Culture, persona.Name, persona.Email);

        return
        [
            // No custom copy, so the gallery shows the standard localized wording rather
            // than one survey's own. The next sample covers the custom-subject/message arm.
            new EmailPreviewSample("survey-invitation", "Survey Invitation",
                emails.SurveyInvitation(email, name, SampleTitle, SampleToken, culture)),
            new EmailPreviewSample("survey-invitation-custom", "Survey Invitation (custom message)",
                emails.SurveyInvitation(email, name, SampleTitle, SampleToken, culture,
                    "Quick favor before Saturday?", "**Thanks** for helping last time — could you fill this in again?")),
            new EmailPreviewSample("survey-reminder", "Survey Reminder",
                emails.SurveyReminder(email, name, SampleTitle, SampleToken, culture)),
        ];
    }
}
