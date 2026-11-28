using NodaTime;
using Xunit;
using System.Reflection;
using AwesomeAssertions;
using Humans.Email.Contracts;
using Humans.Shifts.Services;
using Humans.Shifts.Tests.Infrastructure;

namespace Humans.Shifts.Tests.Services;

/// <summary>
/// Includes the gallery-coverage gate: every template <see cref="ShiftsEmails"/> can build has a
/// sample in <see cref="ShiftsEmailPreviews"/>, so a new template cannot ship invisible.
/// </summary>
public sealed class ShiftsEmailsTests
{
    private static ShiftsEmails Create() => TestShiftsEmails.Create();

    private static CoordinatorRotaMessageRequest RotaRequest(string messageText = "Hello") =>
        new(
            RecipientUserId: Guid.NewGuid(),
            RecipientEmail: "rcpt@x.com",
            RecipientName: "Rcpt",
            SenderName: "Coord",
            SenderEmail: "coord@x.com",
            RotaName: "Gate",
            MessageText: messageText,
            Shifts: [(new LocalDateTime(2026, 7, 6, 9, 0).InUtc(), false)],
            Culture: "en");

    [HumansTheory]
    [MemberData(nameof(SupportedCultures.All), MemberType = typeof(SupportedCultures))]
    public void CoordinatorRotaMessage_RoutesRepliesToCoordinator_VolunteerUpdates(string culture)
    {
        var msg = Create().CoordinatorRotaMessage(RotaRequest() with { Culture = culture });

        msg.Culture.Should().Be(culture);
        msg.RecipientEmail.Should().Be("rcpt@x.com");
        msg.TemplateName.Should().Be("coordinator_rota_message");
        msg.Category.Should().Be(MessageCategory.VolunteerUpdates);
        msg.ReplyTo.Should().Be("coord@x.com");
    }

    [HumansFact]
    public void CoordinatorRotaMessage_DropsShiftSection_WhenShiftsExcluded()
    {
        var withShifts = Create().CoordinatorRotaMessage(RotaRequest()).HtmlBody;
        var without = Create().CoordinatorRotaMessage(RotaRequest() with { IncludeShifts = false }).HtmlBody;

        // The test localizer echoes keys, so the lead-in shows up as its key name.
        withShifts.Should().Contain("<li>Mon July 6 @ 09:00</li>").And.Contain("CoordinatorRotaMessage_ShiftsIntro");
        without.Should().NotContain("<li>").And.NotContain("CoordinatorRotaMessage_ShiftsIntro",
            "the lead-in goes with the list, never stranded above nothing");
        without.Should().Contain("Hello", "the coordinator's own message still ships");
    }

    [HumansFact]
    public void CoordinatorTeamRotasMessage_DropsShiftSection_WhenShiftsExcluded()
    {
        var request = new CoordinatorTeamRotasMessageRequest(
            RecipientUserId: Guid.NewGuid(),
            RecipientEmail: "rcpt@x.com",
            RecipientName: "Rcpt",
            SenderName: "Coord",
            SenderEmail: "coord@x.com",
            TeamName: "Bar Team",
            MessageText: "Thank you",
            ShiftGroups: [new CoordinatorRotaShiftGroup("Gate", [(new LocalDateTime(2026, 7, 6, 9, 0).InUtc(), false)])],
            Culture: "en");

        var without = Create().CoordinatorTeamRotasMessage(request with { IncludeShifts = false }).HtmlBody;

        without.Should().NotContain("Gate").And.NotContain("CoordinatorTeamRotasMessage_ShiftsIntro");
        without.Should().Contain("Thank you");
    }

    [HumansFact]
    public void CoordinatorTeamRotasMessage_RoutesRepliesToCoordinator_VolunteerUpdates()
    {
        var msg = Create().CoordinatorTeamRotasMessage(new CoordinatorTeamRotasMessageRequest(
            RecipientUserId: Guid.NewGuid(),
            RecipientEmail: "rcpt@x.com",
            RecipientName: "Rcpt",
            SenderName: "Coord",
            SenderEmail: "coord@x.com",
            TeamName: "Bar Team",
            MessageText: "Hello",
            ShiftGroups: [new CoordinatorRotaShiftGroup("Gate", [(new LocalDateTime(2026, 7, 6, 9, 0).InUtc(), false)])],
            Culture: "en"));

        msg.TemplateName.Should().Be("coordinator_team_rotas_message");
        msg.Category.Should().Be(MessageCategory.VolunteerUpdates);
        msg.ReplyTo.Should().Be("coord@x.com");
    }

    [HumansFact]
    public void CoordinatorRotaMessage_Body_RendersMarkdown()
    {
        var msg = Create().CoordinatorRotaMessage(
            RotaRequest("**Heads up** — early start\r\n\r\nSee you there"));

        msg.HtmlBody.Should().Contain("<p><strong>Heads up</strong>");
        msg.HtmlBody.Should().Contain("<p>See you there</p>");
        msg.HtmlBody.Should().NotContain("&lt;strong&gt;");
        msg.HtmlBody.Should().NotContain("<br");
    }

    [HumansFact]
    public void CoordinatorTeamRotasMessage_Body_RendersMarkdown()
    {
        var msg = Create().CoordinatorTeamRotasMessage(new CoordinatorTeamRotasMessageRequest(
            RecipientUserId: Guid.NewGuid(),
            RecipientEmail: "rcpt@x.com",
            RecipientName: "Recipient",
            SenderName: "Sender",
            SenderEmail: null,
            TeamName: "Bar Team",
            MessageText: "**Heads up** — early start\r\n\r\nSee you there",
            ShiftGroups: [new CoordinatorRotaShiftGroup("Saturday Bar", [(new LocalDateTime(2026, 7, 11, 10, 0).InUtc(), false)])]));

        msg.HtmlBody.Should().Contain("<p><strong>Heads up</strong>");
        msg.HtmlBody.Should().Contain("<p>See you there</p>");
        msg.HtmlBody.Should().NotContain("&lt;strong&gt;");
        msg.HtmlBody.Should().NotContain("<br");
    }

    [HumansFact]
    public void CoordinatorMessages_Subject_IsNotHtmlEncoded()
    {
        var emails = TestShiftsEmails.Create(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Shifts_Email_CoordinatorRotaMessage_Subject"] = "Message about {0}",
            ["Shifts_Email_CoordinatorTeamRotasMessage_Subject"] = "Message with {0}",
            ["Shifts_Email_CoordinatorRotaMessage_Body"] = "<p>Dear {0},</p><p>From {1} on {2}:</p>{3}{4}{5}",
            ["Shifts_Email_CoordinatorTeamRotasMessage_Body"] = "<p>Dear {0},</p><p>From {1} on {2}:</p>{3}{4}{5}"
        });
        var rota = emails.CoordinatorRotaMessage(RotaRequest() with { RotaName = "Lights & sound" });
        var team = emails.CoordinatorTeamRotasMessage(new CoordinatorTeamRotasMessageRequest(
            Guid.NewGuid(), "rcpt@x.com", "Recipient", "Sender", "coord@x.com", "Bar & kitchen", "Hello",
            [new CoordinatorRotaShiftGroup("Gate", [(new LocalDateTime(2026, 7, 6, 9, 0).InUtc(), false)])], Culture: "en"));

        rota.Subject.Should().Be("Message about Lights & sound");
        rota.HtmlBody.Should().Contain("Lights &amp; sound");
        team.Subject.Should().Be("Message with Bar & kitchen");
        team.HtmlBody.Should().Contain("Bar &amp; kitchen");
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void CoordinatorTemplates_Shifts_SortedWithLocalTimes(bool teamMessage)
    {
        var zone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];
        IReadOnlyList<(ZonedDateTime Start, bool IsAllDay)> shifts =
        [
            (Instant.FromUtc(2026, 10, 25, 1, 15).InZone(zone), false),
            (new LocalDate(2026, 10, 26).AtStartOfDayInZone(zone), true),
            (Instant.FromUtc(2026, 10, 25, 0, 45).InZone(zone), false)
        ];
        var emails = Create();
        var message = teamMessage
            ? emails.CoordinatorTeamRotasMessage(new CoordinatorTeamRotasMessageRequest(
                Guid.NewGuid(), "recipient@example.com", "Recipient", "Sender", null, "Team", "Hello",
                [new CoordinatorRotaShiftGroup("Zebra", shifts), new CoordinatorRotaShiftGroup("alpha", []), new CoordinatorRotaShiftGroup("Alpha", [])], Culture: "en"))
            : emails.CoordinatorRotaMessage(RotaRequest() with { Shifts = shifts });

        const string earlier = "<li>Sun October 25 @ 02:45</li>";
        const string later = "<li>Sun October 25 @ 02:15</li>";
        const string allDay = "<li>Mon October 26</li>";
        message.HtmlBody.Should().Contain(earlier).And.Contain(later).And.Contain(allDay);
        message.HtmlBody.IndexOf(earlier, StringComparison.Ordinal).Should().BeLessThan(message.HtmlBody.IndexOf(later, StringComparison.Ordinal));
        message.HtmlBody.IndexOf(later, StringComparison.Ordinal).Should().BeLessThan(message.HtmlBody.IndexOf(allDay, StringComparison.Ordinal));
        if (teamMessage)
        {
            message.HtmlBody.Should().Contain("<strong>Alpha</strong>").And.Contain("<strong>alpha</strong>").And.Contain("<strong>Zebra</strong>");
            message.HtmlBody.IndexOf("<strong>Alpha</strong>", StringComparison.Ordinal).Should().BeLessThan(
                message.HtmlBody.IndexOf("<strong>alpha</strong>", StringComparison.Ordinal));
            message.HtmlBody.IndexOf("<strong>alpha</strong>", StringComparison.Ordinal).Should().BeLessThan(
                message.HtmlBody.IndexOf("<strong>Zebra</strong>", StringComparison.Ordinal));
        }
    }

    [HumansFact]
    public void Templates_EachHasPreviewSample()
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
    public void PreviewSampleIds_AreUnique()
    {
        var samples = new ShiftsEmailPreviews(Create())
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"));

        samples.Select(s => s.Id).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// Every template the builder can produce, found by reflection rather than listed, so a
    /// template added without a gallery sample fails <see cref="Templates_EachHasPreviewSample"/>.
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
                Guid.NewGuid(), "rcpt@x.com", "Rcpt", "Coord", "coord@x.com", "Bar Team", "Hello",
                [new CoordinatorRotaShiftGroup("Gate", [(new LocalDateTime(2026, 7, 6, 9, 0).InUtc(), false)])], Culture: "en");
        throw new NotSupportedException(
            $"ShiftsEmails.{parameter.Member.Name} takes a {type.Name}; teach this test how to sample one.");
    }
}
