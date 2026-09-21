using Humans.Email.Contracts;

namespace Humans.Tickets.Services;

/// <summary>
/// Tickets' contribution to the template gallery at <c>/Email/EmailPreview</c>: one
/// sample per template <see cref="TicketsEmails"/> can send, built through the same
/// builder the real send path uses, so the gallery cannot drift from what members
/// receive (peterdrier/Humans#1651). Sample data only — no recipient is real and
/// nothing is sent.
/// </summary>
internal sealed class TicketsEmailPreviews(TicketsEmails emails) : IEmailPreviewContributor
{
    private const string SampleTicket = "Nobodies 2026 — Early Bird (TT-10482)";
    private const string SampleReceiver = "Alex Firestone";
    private const string SampleReason = "The receiver could not be verified as a member.";
    private const string SampleReviewUrl = "/Tickets/Admin/Transfers/Detail/00000000-0000-0000-0000-000000000001";

    public IReadOnlyList<EmailPreviewSample> Samples(EmailPreviewPersona persona)
    {
        ArgumentNullException.ThrowIfNull(persona);
        var (culture, name, email) = (persona.Culture, persona.Name, persona.Email);

        return
        [
            new EmailPreviewSample("ticket-transfer-requested", "Ticket Transfer Requested",
                emails.TicketTransferRequested(email, name, SampleReceiver, SampleTicket, culture)),
            new EmailPreviewSample("ticket-transfer-team-notification", "Ticket Transfer — Team Notification",
                emails.TicketTransferTeamNotification(name, SampleReceiver, "alex@example.com", SampleTicket, SampleReason, SampleReviewUrl)),

            // Both branches of the decision, so the gallery shows the cancellation reason
            // rather than only the empty success body.
            new EmailPreviewSample("ticket-transfer-decision-approved", "Ticket Transfer Complete",
                emails.TicketTransferDecision(email, name, successful: true, SampleTicket, SampleReceiver, reason: null, culture)),
            new EmailPreviewSample("ticket-transfer-decision-rejected", "Ticket Transfer Cancelled",
                emails.TicketTransferDecision(email, name, successful: false, SampleTicket, SampleReceiver, SampleReason, culture)),
        ];
    }
}
