using System.Reflection;
using System.Net;
using AwesomeAssertions;
using Humans.Campaigns.Services;
using Humans.Email.Contracts;
using Humans.Users.Contracts;

namespace Humans.Campaigns.Tests.Services;

/// <summary>
/// The per-template routing policy Campaigns stamps on its own messages — template name,
/// opt-out category and the campaign ids the outbox row carries — plus the
/// gallery-coverage gate: every template <see cref="CampaignsEmails"/> can build has a
/// sample in <see cref="CampaignsEmailPreviews"/>, so a new template cannot ship invisible
/// Transport (opt-out, unsubscribe, wrapping, enqueue) stays
/// Email's, covered there.
/// </summary>
public sealed class CampaignsEmailsTests
{
    private static CampaignCodeEmailRequest Request(
        Guid userId, Guid grantId, Guid campaignId,
        string subject = "S {{Name}}", string body = "Hi {{Name}} {{Code}}",
        string code = "ABC", string? replyTo = "reply@x.com") =>
        new(userId, grantId, campaignId, "zoe@x.com", "Zoe", subject, body, code, replyTo);

    [HumansFact]
    public void CampaignCode_CarriesUserGrantReplyToAndCampaignCategory()
    {
        var userId = Guid.NewGuid();
        var grantId = Guid.NewGuid();
        var campaignId = Guid.NewGuid();

        var msg = new CampaignsEmails().CampaignCode(Request(userId, grantId, campaignId));

        msg.RecipientEmail.Should().Be("zoe@x.com");
        msg.TemplateName.Should().Be("campaign_code");
        msg.Category.Should().Be(MessageCategory.CampaignCodes);
        msg.ReplyTo.Should().Be("reply@x.com");
        msg.UserId.Should().Be(userId);
        msg.CampaignGrantId.Should().Be(grantId);
        msg.CampaignId.Should().Be(campaignId);
        msg.Subject.Should().Be("S Zoe");
        msg.HtmlBody.Should().Contain("Zoe").And.Contain("ABC");
    }

    [HumansFact]
    public void CampaignCode_renders_sanitized_markdown_with_https_images()
    {
        var msg = new CampaignsEmails().CampaignCode(new CampaignCodeEmailRequest(
            UserId: Guid.NewGuid(),
            CampaignGrantId: Guid.NewGuid(),
            CampaignId: Guid.NewGuid(),
            RecipientEmail: "zoe@x.com",
            RecipientName: "Daniel <Admin>",
            Subject: "Your code {{Code}}",
            MarkdownBody: "Hi {{Name}}, here is **{{Code}}**.\r\n\r\n[Details](https://example.com)\r\n\r\n![Poster](https://example.com/poster.png)\r\n<script>alert('x')</script>",
            Code: "ABC123",
            ReplyTo: null));

        msg.Subject.Should().Be("Your code ABC123");
        msg.HtmlBody.Should().Contain("Daniel &lt;Admin&gt;");
        msg.HtmlBody.Should().Contain("<strong>ABC123</strong>");
        msg.HtmlBody.Should().Contain("<a href=\"https://example.com\">Details</a>");
        msg.HtmlBody.Should().NotContain("<script>");
        msg.HtmlBody.Should().Contain("<img src=\"https://example.com/poster.png\"");
    }

    [HumansFact]
    public void CampaignCode_DoesNotExpandPlaceholdersInsideInsertedValues()
    {
        var request = Request(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            subject: "{{Code}} / {{Name}} / {{Code}} / {{code}}",
            body: "**{{Code}}** / {{Name}}", code: "Value {{Name}} & $1")
            with
        { RecipientName = "Name {{Code}} <Admin>" };
        var message = new CampaignsEmails().CampaignCode(request);
        message.Subject.Should().Be("Value {{Name}} & $1 / Name {{Code}} <Admin> / Value {{Name}} & $1 / {{code}}");
        message.HtmlBody.Should().Contain("<strong>Value {{Name}} &amp; $1</strong>")
            .And.Contain("Name {{Code}} &lt;Admin&gt;");
    }

    [HumansTheory]
    [Xunit.InlineData("Hi {{Name}} / {{Code}}")]
    [Xunit.InlineData("`{{Name}}` / `{{Code}}`")]
    [Xunit.InlineData("```\n{{Name}} / {{Code}}\n```")]
    public void CampaignCode_InsertedValuesRemainLiteralMarkdownData(string body)
    {
        const string name = "![portrait](https://example.org/tracker.png)";
        const string code = "`[redeem](https://example.org/redirect)`";
        var request = Request(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), body: body, code: code)
            with
        { RecipientName = name };

        var message = new CampaignsEmails().CampaignCode(request);

        message.HtmlBody.Should().NotContain("<img").And.NotContain("<a ");
        message.HtmlBody.Should().Contain(name).And.Contain(code);
    }

    [HumansTheory]
    [Xunit.InlineData("[Redeem]({{Code}})", "javascript:alert(1)", false)]
    [Xunit.InlineData("![{{Name}}]({{Code}})", "http://example.org/tracker.png", false)]
    [Xunit.InlineData("![{{Name}}]({{Code}})", "https://example.org/poster.png", true)]
    public void CampaignCode_AuthoredDestinationsAreSanitizedAfterSubstitution(string body, string url, bool allowed)
    {
        var message = new CampaignsEmails().CampaignCode(Request(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), body: body, code: url));

        message.HtmlBody.Contains(url, StringComparison.Ordinal).Should().Be(allowed);
    }

    [HumansTheory]
    [Xunit.InlineData(false, 899)]
    [Xunit.InlineData(false, 900)]
    [Xunit.InlineData(true, 899)]
    [Xunit.InlineData(true, 900)]
    public void CampaignCode_BoundsExpandedSubjectWithoutLosingItsContents(bool astral, int prefixLength)
    {
        var code = string.Concat(Enumerable.Repeat(astral ? "🎟" : "X", 100));
        var prefix = new string('S', prefixLength) + " ";
        var fullSubject = prefix + code;
        var request = Request(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            subject: prefix + "{{Code}}", body: "Ready.", code: code);

        var message = new CampaignsEmails().CampaignCode(request);

        message.Subject.EnumerateRunes().Count().Should().Be(1000);
        if (prefixLength == 899)
        {
            message.Subject.Should().Be(fullSubject);
            message.HtmlBody.Should().Be("<p>Ready.</p>\n");
        }
        else
        {
            message.Subject.Should().EndWith("…");
            WebUtility.HtmlDecode(message.HtmlBody).Should().Contain(fullSubject).And.Contain("Ready.");
        }
    }

    [HumansFact]
    public void EveryTemplateHasAPreviewSample()
    {
        var emails = new CampaignsEmails();
        var templates = TemplateNames(emails);
        templates.Should().NotBeEmpty();

        var sampled = new CampaignsEmailPreviews(emails)
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"))
            .Select(s => s.Message.TemplateName);

        sampled.Should().BeEquivalentTo(templates,
            "every Campaigns template needs a row in /Email/EmailPreview");
    }

    [HumansFact]
    public void PreviewSampleIdsAreUnique()
    {
        var samples = new CampaignsEmailPreviews(new CampaignsEmails())
            .Samples(new EmailPreviewPersona("en", "Sally Smith", "sally@example.com"));

        samples.Select(s => s.Id).Should().OnlyHaveUniqueItems();
    }

    /// <summary>
    /// Every template the builder can produce, found by reflection rather than listed, so a
    /// template added without a gallery sample fails <see cref="EveryTemplateHasAPreviewSample"/>.
    /// </summary>
    private static IReadOnlyList<string> TemplateNames(CampaignsEmails emails) =>
    [
        .. typeof(CampaignsEmails)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.ReturnType == typeof(EmailMessage))
            .Select(m => ((EmailMessage)m.Invoke(emails, [.. m.GetParameters().Select(SampleArgument)])!).TemplateName)
    ];

    private static object SampleArgument(ParameterInfo parameter)
    {
        var type = parameter.ParameterType;
        if (type == typeof(CampaignCodeEmailRequest)) return Request(Guid.Empty, Guid.Empty, Guid.Empty);
        throw new NotSupportedException(
            $"CampaignsEmails.{parameter.Member.Name} takes a {type.Name}; teach this test how to sample one.");
    }
}
