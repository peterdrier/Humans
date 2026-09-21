using Humans.Base.Extensions;
using Humans.Email.Contracts;
using AwesomeAssertions;
using Humans.Base.Configuration;
using Humans.Email.Services;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Humans.Email.Tests.Services;

public sealed class EmailRendererTests
{
    [HumansFact]
    public void FacilitatedMessage_renders_markdown_instead_of_html_encoded_plain_text()
    {
        var renderer = CreateRenderer();

        var content = renderer.RenderFacilitatedMessage(
            "Recipient",
            "Sender",
            "**Hi there** — see [this](https://example.com)\r\n\r\nSecond line",
            false,
            null);

        content.HtmlBody.Should().Contain("<p><strong>Hi there</strong>");
        content.HtmlBody.Should().Contain("<a href=\"https://example.com\">this</a>");
        content.HtmlBody.Should().Contain("<p>Second line</p>");
        content.HtmlBody.Should().NotContain("&lt;strong&gt;");
        content.HtmlBody.Should().NotContain("<br");
    }

    [HumansFact]
    public void WorkgroupNotice_EveryKindEveryCulture_RendersNonEmptyContentWithNoRawKeyLeak()
    {
        var renderer = CreateRealRenderer();

        foreach (var culture in CultureCatalog.SupportedCultureCodes)
        {
            foreach (var kind in Enum.GetValues<WorkgroupNoticeKind>())
            {
                var request = new WorkgroupNoticeRequest(
                    RecipientEmail: "coord@x.com",
                    RecipientName: "Coord",
                    Kind: kind,
                    WorkgroupName: "Safety",
                    WorkgroupSlug: "safety",
                    Detail: "Some detail",
                    Culture: culture);

                var content = renderer.RenderWorkgroupNotice(request);

                content.Subject.Should().NotBeNullOrWhiteSpace(because: $"{kind}/{culture} subject");
                content.HtmlBody.Should().NotBeNullOrWhiteSpace(because: $"{kind}/{culture} body");
                content.Subject.Should().NotContain("Email_", because: $"{kind}/{culture} subject leaked a raw key");
                content.HtmlBody.Should().NotContain("Email_", because: $"{kind}/{culture} body leaked a raw key");
                content.HtmlBody.Should().Contain("safety", because: "the working-group link is built from the slug");
            }
        }
    }

    [HumansFact]
    public void WorkgroupNotice_DetailLine_AppearsWhenSuppliedAndBodyIsWellFormedWhenNull()
    {
        var renderer = CreateRealRenderer();

        var withReason = renderer.RenderWorkgroupNotice(new WorkgroupNoticeRequest(
            "a@x.com", "Alice", WorkgroupNoticeKind.Refused, "Safety", "safety", Detail: "not enough scope", Culture: "en"));
        withReason.HtmlBody.Should().Contain("not enough scope");

        var withoutReason = renderer.RenderWorkgroupNotice(new WorkgroupNoticeRequest(
            "a@x.com", "Alice", WorkgroupNoticeKind.Refused, "Safety", "safety", Detail: null, Culture: "en"));
        withoutReason.HtmlBody.Should().NotBeNullOrWhiteSpace();
        withoutReason.HtmlBody.Should().NotContain("{2}");
    }

    [HumansFact]
    public void WorkgroupNotice_NullRecipientName_UsesGenericGreeting()
    {
        var renderer = CreateRealRenderer();

        var content = renderer.RenderWorkgroupNotice(new WorkgroupNoticeRequest(
            "board@x.com", null, WorkgroupNoticeKind.Applied, "Safety", "safety", Culture: "en"));

        content.HtmlBody.Should().Contain("Hello,");
        content.HtmlBody.Should().NotContain("Hi ,");
    }

    [HumansFact]
    public void WorkgroupNotice_CultureSelectsLanguage()
    {
        var renderer = CreateRealRenderer();

        var en = renderer.RenderWorkgroupNotice(new WorkgroupNoticeRequest(
            "a@x.com", "Alice", WorkgroupNoticeKind.Registered, "Safety", "safety", Culture: "en"));
        var es = renderer.RenderWorkgroupNotice(new WorkgroupNoticeRequest(
            "a@x.com", "Alice", WorkgroupNoticeKind.Registered, "Safety", "safety", Culture: "es"));

        en.Subject.Should().Be("Working group registered: Safety");
        es.Subject.Should().Be("Grupo de trabajo registrado: Safety");
    }

    [HumansFact]
    public void WorkgroupNotice_NameWithAnAmpersand_StaysRawInTheSubjectAndEncodedInTheBody()
    {
        var renderer = CreateRealRenderer();

        var content = renderer.RenderWorkgroupNotice(new WorkgroupNoticeRequest(
            "board@x.com", "Ada", WorkgroupNoticeKind.Applied, "Health & Safety", "health-safety",
            Culture: "en"));

        // The subject is plain text; the body is HTML. One encoded value cannot serve both.
        content.Subject.Should().Contain("Health & Safety").And.NotContain("&amp;");
        content.HtmlBody.Should().Contain("Health &amp; Safety");
    }

    private static EmailRenderer CreateRealRenderer()
    {
        var factory = new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance);
        var localizer = new StringLocalizer<EmailResource>(factory);

        return new EmailRenderer(
            Options.Create(new EmailSettings { BaseUrl = "https://humans.example" }),
            localizer,
            NullLogger<EmailRenderer>.Instance);
    }

    private static EmailRenderer CreateRenderer()
    {
        var strings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Email_FacilitatedMessage_Body"] =
                "<p>Hi {0},</p><p>{1} sent you a message:</p>{2}{3}",
        };
        var localizer = Substitute.For<IStringLocalizer<EmailResource>>();
        localizer[Arg.Any<string>()].Returns(call =>
        {
            var key = call.Arg<string>();
            return new LocalizedString(key, strings.GetValueOrDefault(key, key));
        });

        return new EmailRenderer(
            Options.Create(new EmailSettings { BaseUrl = "https://humans.example" }),
            localizer,
            NullLogger<EmailRenderer>.Instance);
    }
}
