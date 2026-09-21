using System.Reflection;
using AwesomeAssertions;
using Humans.Email.Contracts;
using Humans.Shifts.Services;
using Humans.Shifts.Tests.Infrastructure;
using Humans.Users.Contracts;

namespace Humans.Shifts.Tests.Services;

/// <summary>
/// The per-template routing policy Shifts stamps on its own coordinator messages —
/// template name, opt-out category and the coordinator reply-to — the markdown body path,
/// plus the gallery-coverage gate: every template <see cref="ShiftsEmails"/> can build has
/// a sample in <see cref="ShiftsEmailPreviews"/>, so a new template cannot ship invisible
/// (peterdrier/Humans#1651). Transport (opt-out, unsubscribe, wrapping, enqueue) stays
/// Email's, covered there.
/// </summary>
public sealed class ShiftsEmailsTests
{
    private static ShiftsEmails Create() => TestShiftsEmails.Create();

    private static CoordinatorRotaMessageRequest RotaRequest(string messageText = "Hello") =>
        new(
            RecipientEmail: "rcpt@x.com",
            RecipientName: "Rcpt",
            SenderName: "Coord",
            SenderEmail: "coord@x.com",
            RotaName: "Gate",
            MessageText: messageText,
            ShiftLines: ["Mon"],
            Culture: "en");

    [HumansFact]
    public void CoordinatorRotaMessage_RoutesRepliesToCoordinator_VolunteerUpdates()
    {
        var msg = Create().CoordinatorRotaMessage(RotaRequest());

        msg.RecipientEmail.Should().Be("rcpt@x.com");
        msg.TemplateName.Should().Be("coordinator_rota_message");
        msg.Category.Should().Be(MessageCategory.VolunteerUpdates);
        msg.ReplyTo.Should().Be("coord@x.com");
    }

    [HumansFact]
    public void CoordinatorTeamRotasMessage_RoutesRepliesToCoordinator_VolunteerUpdates()
    {
        var msg = Create().CoordinatorTeamRotasMessage(new CoordinatorTeamRotasMessageRequest(
            RecipientEmail: "rcpt@x.com",
            RecipientName: "Rcpt",
            SenderName: "Coord",
            SenderEmail: "coord@x.com",
            TeamName: "Bar Team",
            MessageText: "Hello",
            ShiftGroups: [new CoordinatorRotaShiftGroup("Gate", ["Mon"])],
            Culture: "en"));

        msg.TemplateName.Should().Be("coordinator_team_rotas_message");
        msg.Category.Should().Be(MessageCategory.VolunteerUpdates);
        msg.ReplyTo.Should().Be("coord@x.com");
    }

    [HumansFact]
    public void CoordinatorRotaMessage_renders_markdown_instead_of_html_encoded_plain_text()
    {
        var msg = Create().CoordinatorRotaMessage(
            RotaRequest("**Heads up** — early start\r\n\r\nSee you there"));

        msg.HtmlBody.Should().Contain("<p><strong>Heads up</strong>");
        msg.HtmlBody.Should().Contain("<p>See you there</p>");
        msg.HtmlBody.Should().NotContain("&lt;strong&gt;");
        msg.HtmlBody.Should().NotContain("<br");
    }

    [HumansFact]
    public void CoordinatorTeamRotasMessage_renders_markdown_instead_of_html_encoded_plain_text()
    {
        var msg = Create().CoordinatorTeamRotasMessage(new CoordinatorTeamRotasMessageRequest(
            RecipientEmail: "rcpt@x.com",
            RecipientName: "Recipient",
            SenderName: "Sender",
            SenderEmail: null,
            TeamName: "Bar Team",
            MessageText: "**Heads up** — early start\r\n\r\nSee you there",
            ShiftGroups: [new CoordinatorRotaShiftGroup("Saturday Bar", ["Saturday 10:00-14:00"])]));

        msg.HtmlBody.Should().Contain("<p><strong>Heads up</strong>");
        msg.HtmlBody.Should().Contain("<p>See you there</p>");
        msg.HtmlBody.Should().NotContain("&lt;strong&gt;");
        msg.HtmlBody.Should().NotContain("<br");
    }

    [HumansFact]
    public void EveryTemplateHasAPreviewSample()
    {
        var emails = Create();
        var templates = TemplateNames(emails);
        templates.Should().NotBeEmpty();

        var sampled = new ShiftsEmailPreviews(emails)
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"))
            .Select(s => s.Message.TemplateName);

        sampled.Should().BeEquivalentTo(templates,
            "every Shifts template needs a row in /Email/EmailPreview");
    }

    [HumansFact]
    public void PreviewSampleIdsAreUnique()
    {
        var samples = new ShiftsEmailPreviews(Create())
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"));

        samples.Select(s => s.Id).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// Every template the builder can produce, found by reflection rather than listed, so a
    /// template added without a gallery sample fails <see cref="EveryTemplateHasAPreviewSample"/>.
    /// </summary>
    private static IReadOnlyList<string> TemplateNames(ShiftsEmails emails) =>
    [
        .. typeof(ShiftsEmails)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.ReturnType == typeof(EmailMessage))
            .Select(m => ((EmailMessage)m.Invoke(emails, [.. m.GetParameters().Select(SampleArgument)])!).TemplateName)
    ];

    private static object SampleArgument(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        if (type == typeof(CoordinatorRotaMessageRequest)) return RotaRequest();
        if (type == typeof(CoordinatorTeamRotasMessageRequest))
            return new CoordinatorTeamRotasMessageRequest(
                "rcpt@x.com", "Rcpt", "Coord", "coord@x.com", "Bar Team", "Hello",
                [new CoordinatorRotaShiftGroup("Gate", ["Mon"])], "en");
        throw new NotSupportedException(
            $"ShiftsEmails.{parameter.Member.Name} takes a {type.Name}; teach this test how to sample one.");
    }
}
