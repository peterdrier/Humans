using Humans.Email.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Domain.Checkout;
using Humans.Tickets.Services.Delivery;

namespace Humans.Tickets.Services;

internal sealed class TicketsEmailPreviews(TicketsEmails emails, TicketCodeImages codeImages) : IEmailPreviewContributor
{
    private const string SampleReceiver = "Alex Firestone";
    private const string SampleEvent = "Elsewhere 2027";
    private const string SampleBarcode = "H7K2M9QX4T8VB3NRP";
    private const string SampleChildBarcode = "HC4W8N2R6T0YZ5KMB";
    private static readonly Uri SampleBase = new("https://humans.nobodies.team/");

    public IReadOnlyList<EmailPreviewSample> Samples(EmailPreviewPersona persona)
    {
        ArgumentNullException.ThrowIfNull(persona);
        var (culture, name, email) = (persona.Culture, persona.Name, persona.Email);

        return
        [
            new EmailPreviewSample("ticket-resale-invitation", "Ticket Resale Invitation",
                emails.ResaleInvitation(email, SampleReceiver, "Elsewhere 2027", "https://humans.example/Tickets/Buy/Resale/sample-token", culture)),

            new EmailPreviewSample("ticket-issued", "Ticket (bought by someone else, with a child)",
                emails.TicketIssued(new IssuedTicketMail(Guid.Empty, email, name, SampleEvent,
                    [
                        new TicketCard(name, "Adult", SampleBarcode, codeImages.ImageUrl(SampleBarcode)),
                        new TicketCard("Kit Firestone", "Child", SampleChildBarcode, codeImages.ImageUrl(SampleChildBarcode)),
                    ],
                    new Uri(SampleBase, "Tickets/Buy/Mine"), SampleReceiver, null), culture)),
            new EmailPreviewSample("ticket-order-receipt", "Ticket Order Receipt",
                emails.OrderReceipt(new OrderReceiptMail(Guid.Empty, email, name, SampleEvent, "3F9A0C1B",
                    new NodaTime.LocalDate(2027, 1, 2), "card",
                    [
                        new TicketCheckoutOrderLine { Kind = CheckoutLineKind.Ticket, AmountMinor = 22500, VatRate = 0.10m, AttendeeName = name },
                        new TicketCheckoutOrderLine { Kind = CheckoutLineKind.Ticket, AmountMinor = 22500, VatRate = 0.10m, AttendeeName = SampleReceiver },
                        new TicketCheckoutOrderLine { Kind = CheckoutLineKind.Donation, AmountMinor = 17500, DonationPurpose = TicketDonationPurpose.General },
                    ],
                    62500, 0, new Uri(SampleBase, "Tickets/Buy/Order/00000000-0000-0000-0000-000000000001"), null), culture)),
            new EmailPreviewSample("ticket-join-invitation", "Ticket Holder Volunteer Invitation",
                emails.JoinInvitation(Guid.Empty, email, name, SampleEvent, new Uri(SampleBase, "Profile"),
                    new Uri(SampleBase, "Shifts"), null, culture)),
            new EmailPreviewSample("ticket-event-cancelled", "Ticket Event Cancelled",
                emails.EventCancelled(Guid.Empty, email, name, SampleEvent, 45000, hadDonation: true, culture)),
        ];
    }
}
