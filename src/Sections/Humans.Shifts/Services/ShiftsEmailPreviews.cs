using NodaTime;
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
                    [(new LocalDateTime(2026, 7, 11, 10, 0).InUtc(), false), (new LocalDateTime(2026, 7, 11, 18, 0).InUtc(), false)], Culture: culture))),
            new EmailPreviewSample("coordinator-team-rotas-message", "Coordinator Team Rotas Message",
                emails.CoordinatorTeamRotasMessage(new CoordinatorTeamRotasMessageRequest(
                    Guid.Empty, email, name, SampleSender, SampleSenderEmail,
                    "Bar Team", SampleMessage,
                    [
                        new CoordinatorRotaShiftGroup("Saturday Bar", [(new LocalDateTime(2026, 7, 11, 10, 0).InUtc(), false)]),
                        new CoordinatorRotaShiftGroup("Sunday Bar", [(new LocalDateTime(2026, 7, 12, 12, 0).InUtc(), false)]),
                    ],
                    Culture: culture))),
        ];
    }
}
