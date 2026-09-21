using System.Reflection;
using AwesomeAssertions;
using Humans.Email.Contracts;
using Humans.Tickets.Contracts;
using Humans.Tickets.Services;
using Humans.Tickets.Tests.Infrastructure;
using Humans.Users.Contracts;

namespace Humans.Tickets.Tests.Services;

/// <summary>
/// The per-template routing policy Tickets stamps on its own messages — template name,
/// opt-out category and the ticket-team inbox — plus the gallery-coverage gate: every
/// template <see cref="TicketsEmails"/> can build has a sample in
/// <see cref="TicketsEmailPreviews"/>, so a new template cannot ship invisible
/// (peterdrier/Humans#1651). Transport (opt-out, unsubscribe, wrapping, enqueue) stays
/// Email's, covered there.
/// </summary>
public sealed class TicketsEmailsTests
{
    private static TicketsEmails Create() => TestTicketsEmails.Create();

    [HumansFact]
    public void TicketTransferRequested_GoesToTheSender_System()
    {
        var msg = Create().TicketTransferRequested("a@x.com", "Alice", "Receiver", "Ticket #1", "en");

        msg.RecipientEmail.Should().Be("a@x.com");
        msg.RecipientName.Should().Be("Alice");
        msg.TemplateName.Should().Be("ticket_transfer_requested");
        msg.Category.Should().Be(MessageCategory.System);
        msg.ReplyTo.Should().BeNull();
    }

    [HumansFact]
    public void TicketTransferTeamNotification_RoutesToTicketsInbox_System()
    {
        var msg = Create().TicketTransferTeamNotification(
            "Sender", "Receiver", "rx@x.com", "Ticket #1", "reason", "https://review");

        msg.RecipientEmail.Should().Be(TicketConstants.TicketsTeamEmail);
        msg.RecipientName.Should().Be("Ticket team");
        msg.TemplateName.Should().Be("ticket_transfer_team");
        msg.Category.Should().Be(MessageCategory.System);
    }

    [HumansFact]
    public void TicketTransferTeamNotification_MakesARelativeReviewUrlAbsolute()
    {
        var msg = Create().TicketTransferTeamNotification(
            "Sender", "Receiver", "rx@x.com", "Ticket #1", null, "/Tickets/Admin/Transfers/Detail/1");

        msg.HtmlBody.Should().Contain($"{TestTicketsEmails.BaseUrl}/Tickets/Admin/Transfers/Detail/1");
    }

    [HumansFact]
    public void TicketTransferDecision_TemplateReflectsOutcome()
    {
        var emails = Create();

        emails.TicketTransferDecision("a@x.com", "A", successful: true, "T", "Rx", null, "en")
            .TemplateName.Should().Be("ticket_transfer_completed");

        emails.TicketTransferDecision("a@x.com", "A", successful: false, "T", "Rx", "why", "en")
            .TemplateName.Should().Be("ticket_transfer_cancelled");
    }

    [HumansFact]
    public void TicketTransferDecision_EncodesNamesAndOmitsAnEmptyReason()
    {
        var emails = Create();

        emails.TicketTransferDecision("a@x.com", "Ann <Admin>", successful: false, "Lights & sound", "Rx", null, "en")
            .HtmlBody.Should().Contain("Ann &lt;Admin&gt;").And.Contain("Lights &amp; sound")
            .And.NotContain("<strong>Reason:</strong>");

        emails.TicketTransferDecision("a@x.com", "Ann", successful: false, "T", "Rx", "sold out", "en")
            .HtmlBody.Should().Contain("<strong>Reason:</strong> sold out");
    }

    [HumansFact]
    public void EveryTemplateHasAPreviewSample()
    {
        var emails = Create();
        var templates = TemplateNames(emails);
        templates.Should().NotBeEmpty();

        var sampled = new TicketsEmailPreviews(emails)
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"))
            .Select(s => s.Message.TemplateName);

        sampled.Should().BeEquivalentTo(templates,
            "every Tickets template needs a row in /Email/EmailPreview");
    }

    [HumansFact]
    public void PreviewSampleIdsAreUnique()
    {
        var samples = new TicketsEmailPreviews(Create())
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
        if (type == typeof(bool)) return flag;
        if (type == typeof(string)) return string.Equals(parameter.Name, "culture", StringComparison.Ordinal) ? "en" : "sample";
        throw new NotSupportedException(
            $"TicketsEmails.{parameter.Member.Name} takes a {type.Name}; teach this test how to sample one.");
    }
}
