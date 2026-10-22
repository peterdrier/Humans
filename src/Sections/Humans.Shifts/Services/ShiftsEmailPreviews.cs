using Humans.Email.Contracts;

namespace Humans.Shifts.Services;

/// <summary>
/// Built through the real send path's builder, so the gallery cannot drift from what volunteers receive.
/// </summary>
internal sealed class ShiftsEmailPreviews(ShiftsEmails emails) : IEmailPreviewContributor
{
    private const string SampleSender = "Alex Firestone";
    private const string SampleSenderEmail = "alex@example.com";

    private const string SampleMessage =
        "Thanks for signing up! Please arrive ten minutes early for the handover.";

    public IReadOnlyList<EmailPreviewSample> Samples(EmailPreviewPersona persona)
    {
        ArgumentNullException.ThrowIfNull(persona);
        var (culture, name, email) = (persona.Culture, persona.Name, persona.Email);

        return
        [
            new EmailPreviewSample("coordinator-rota-message", "Coordinator Rota Message",
                emails.CoordinatorRotaMessage(new CoordinatorRotaMessageRequest(
                    Guid.Empty, email, name, SampleSender, SampleSenderEmail,
                    "Saturday Bar", SampleMessage,
                    ["Saturday 10:00-14:00", "Saturday 18:00-22:00"], Culture: culture))),
            new EmailPreviewSample("coordinator-team-rotas-message", "Coordinator Team Rotas Message",
                emails.CoordinatorTeamRotasMessage(new CoordinatorTeamRotasMessageRequest(
                    Guid.Empty, email, name, SampleSender, SampleSenderEmail,
                    "Bar Team", SampleMessage,
                    [
                        new CoordinatorRotaShiftGroup("Saturday Bar", ["Saturday 10:00-14:00"]),
                        new CoordinatorRotaShiftGroup("Sunday Bar", ["Sunday 12:00-16:00"]),
                    ],
                    Culture: culture))),
        ];
    }
}
