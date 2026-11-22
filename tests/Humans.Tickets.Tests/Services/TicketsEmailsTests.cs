using System.Reflection;
using AwesomeAssertions;
using Humans.Email.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Domain.Checkout;
using Humans.Tickets.Services;
using Humans.Tickets.Tests.Infrastructure;

namespace Humans.Tickets.Tests.Services;

/// <summary>
/// Includes the gallery-coverage gate: every template <see cref="TicketsEmails"/> can build has a sample in <see cref="TicketsEmailPreviews"/>, so a new template cannot ship invisible.
/// </summary>
public sealed class TicketsEmailsTests
{
    private static TicketsEmails Create() => TestTicketsEmails.Create();

    [HumansFact]
    public void ReconciliationAlert_GoesToTheTicketsInbox_WithEachFindingEncoded()
    {
        var msg = Create().ReconciliationAlert(["boxoffice orders: <out> of balance", "pretix orders: could not be checked"],
            "/Tickets/Admin/Sales/Recon?from=2027-01-01&to=2027-01-09");

        msg.RecipientEmail.Should().Be(TicketConstants.TicketsTeamEmail);
        msg.UserId.Should().BeNull();
        msg.Category.Should().Be(MessageCategory.System);
        msg.TemplateName.Should().Be("ticket_reconciliation_alert");
        msg.HtmlBody.Should().Contain("<li>boxoffice orders: &lt;out&gt; of balance</li>")
            .And.Contain("<li>pretix orders: could not be checked</li>")
            .And.Contain($"{TestTicketsEmails.BaseUrl}/Tickets/Admin/Sales/Recon?from=2027-01-01&amp;to=2027-01-09");
    }

    private static IssuedTicketMail SampleTicket(string holderName = "Ana", string? buyerName = null, params TicketCard[] riders) => new(
        Guid.NewGuid(), "ana@x.com", holderName, "Nowhere 2027",
        [new TicketCard(holderName, "Adult", "H7K2M9QX4T8VB3NRP", new Uri("https://humans.example/Tickets/Code/tok")), .. riders],
        new Uri("https://humans.example/Tickets/Buy/Mine"), buyerName, Guid.NewGuid());

    private static OrderReceiptMail SampleReceipt(long refundedMinor = 0) => new(
        Guid.NewGuid(), "payer@x.com", "Payer", "Nowhere 2027", "3F9A0C1B", new NodaTime.LocalDate(2027, 1, 2), "card",
        [
            new TicketCheckoutOrderLine { Kind = OrderLineKind.Ticket, AmountMinor = 22500, VatRate = 0.10m, HolderName = "Ana <A>" },
            new TicketCheckoutOrderLine { Kind = OrderLineKind.Donation, AmountMinor = 17500, DonationPurpose = "More low income tickets" },
        ],
        40000, refundedMinor, new Uri("https://humans.example/Tickets/Buy/Order/1"), Guid.NewGuid());

    [HumansFact]
    public void TicketIssued_GoesToTheHolder_WithTheCodeImageAndRules_System()
    {
        var ticket = SampleTicket("Ana <Holder>", buyerName: "Ben & Co");

        var msg = Create().TicketIssued(ticket, "en");

        msg.RecipientEmail.Should().Be("ana@x.com");
        msg.UserId.Should().Be(ticket.HolderUserId);
        msg.Category.Should().Be(MessageCategory.System, "a ticket is not mail anyone may opt out of");
        msg.TemplateName.Should().Be("ticket_issued");
        msg.Urgent.Should().BeTrue("the buyer is waiting for their ticket");
        msg.DeliveryId.Should().Be(ticket.DeliveryId);
        msg.Subject.Should().Be("Your ticket for Nowhere 2027");
        msg.HtmlBody.Should().Contain("src=\"https://humans.example/Tickets/Code/tok\"")
            .And.Contain("H7K2M9QX4T8VB3NRP")
            .And.Contain("Ana &lt;Holder&gt;").And.NotContain("Ana <Holder>")
            .And.Contain("Ben &amp; Co bought this ticket for you")
            .And.Contain("Adult ticket")
            .And.Contain("Ticket rules").And.Contain("photo ID");
    }

    [HumansFact]
    public void TicketIssued_CarriesAChildTicketRidingOnTheAdults()
    {
        var msg = Create().TicketIssued(SampleTicket(riders: new TicketCard("Kid", "Child", "HKID0000000000000", new Uri("https://humans.example/Tickets/Code/kid"))), "en");

        msg.HtmlBody.Should().Contain("HKID0000000000000").And.Contain("Child ticket").And.Contain("children coming with you")
            .And.Contain("H7K2M9QX4T8VB3NRP");
    }

    [HumansFact]
    public void TicketIssued_ForTheBuyerThemselves_SaysNothingAboutWhoPaid()
    {
        Create().TicketIssued(SampleTicket(), "en").HtmlBody.Should().NotContain("bought this ticket for you")
            .And.NotContain("children coming with you");
    }

    [HumansFact]
    public void TicketIssued_RendersInTheHolderCulture()
    {
        Create().TicketIssued(SampleTicket(), "es").Subject.Should().Be("Tu entrada para Nowhere 2027");
    }

    [HumansFact]
    public void OrderReceipt_SplitsTheTicketWithItsVatFromTheDonationWithout()
    {
        var msg = Create().OrderReceipt(SampleReceipt(), "en");

        msg.RecipientEmail.Should().Be("payer@x.com");
        msg.Category.Should().Be(MessageCategory.System);
        msg.TemplateName.Should().Be("ticket_order_receipt");
        msg.Urgent.Should().BeTrue();
        msg.Subject.Should().Be("Receipt for your order 3F9A0C1B");
        msg.HtmlBody.Should().Contain("Ticket for Ana &lt;A&gt; (includes 10% VAT)")
            .And.Contain("Donation: More low income tickets (no VAT)")
            .And.Contain("225.00 €").And.Contain("175.00 €").And.Contain("400.00 €")
            .And.Contain("not an invoice")
            .And.Contain("Payment method: card")
            .And.NotContain("Refunded");
    }

    [HumansFact]
    public void OrderReceipt_ShowsAnEarlierRefund()
    {
        Create().OrderReceipt(SampleReceipt(refundedMinor: 22500), "en").HtmlBody.Should().Contain("Refunded").And.Contain("-225.00 €");
    }

    [HumansFact]
    public void JoinInvitation_IsAVolunteerUpdate_AndLinksProfileAndShifts()
    {
        var msg = Create().JoinInvitation(Guid.NewGuid(), "ana@x.com", "Ana", "Nowhere 2027",
            new Uri("https://humans.example/Profile"), new Uri("https://humans.example/Shifts"), null, "en");

        msg.Category.Should().Be(MessageCategory.VolunteerUpdates);
        msg.TemplateName.Should().Be("ticket_join_invitation");
        msg.HtmlBody.Should().Contain("href=\"https://humans.example/Profile\"").And.Contain("href=\"https://humans.example/Shifts\"");
    }

    [HumansFact]
    public void EveryTemplateHasAPreviewSample()
    {
        var emails = Create();
        var templates = TemplateNames(emails);
        templates.Should().NotBeEmpty();

        var sampled = new TicketsEmailPreviews(emails, TestTicketCodeImages.Create())
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"))
            .Select(s => s.Message.TemplateName).Distinct(StringComparer.Ordinal);

        sampled.Should().BeEquivalentTo(templates,
            "every Tickets template needs a row in /Email/EmailPreview");
    }

    [HumansFact]
    public void PreviewSampleIdsAreUnique()
    {
        var samples = new TicketsEmailPreviews(Create(), TestTicketCodeImages.Create())
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"));

        samples.Select(s => s.Id).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// Every template the builder can produce, found by reflection rather than listed, so a
    /// template added without a gallery sample fails <see cref="EveryTemplateHasAPreviewSample"/>.
    /// The decision builder is one method over two outcomes, so each <c>bool</c> arm is sampled.
    /// </summary>
    private static IReadOnlyList<string> TemplateNames(TicketsEmails emails) =>
    [
        .. typeof(TicketsEmails)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.ReturnType == typeof(EmailMessage))
            .SelectMany(m => new[] { true, false }.Select(flag =>
                ((EmailMessage)m.Invoke(emails, [.. m.GetParameters().Select(p => SampleArgument(p, flag))])!).TemplateName))
            .Distinct(StringComparer.Ordinal)
    ];

    private static object SampleArgument(ParameterInfo parameter, bool flag)
    {
        var type = parameter.ParameterType;
        if (type == typeof(Guid) || type == typeof(Guid?)) return Guid.NewGuid();
        if (type == typeof(Uri)) return new Uri("https://humans.example/x");
        if (type == typeof(IssuedTicketMail)) return SampleTicket(buyerName: flag ? "Payer" : null);
        if (type == typeof(OrderReceiptMail)) return SampleReceipt();
        if (type == typeof(bool)) return flag;
        if (type == typeof(int)) return 3;
        if (type == typeof(long)) return 22500L;
        if (type == typeof(IReadOnlyList<string>)) return new[] { "sample" };
        if (type == typeof(string)) return string.Equals(parameter.Name, "culture", StringComparison.Ordinal) ? "en" : "sample";
        throw new NotSupportedException(
            $"TicketsEmails.{parameter.Member.Name} takes a {type.Name}; teach this test how to sample one.");
    }
}
