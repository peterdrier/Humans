using AwesomeAssertions;
using Humans.Base.Configuration;
using Humans.Base.Extensions;
using Humans.Email.Contracts;
using Humans.Email.Services;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Humans.Email.Tests.Services;

public sealed class EmailRendererTests
{
    [HumansFact]
    public void SurveyInvitation_custom_copy_renders_sanitized_markdown_without_images()
    {
        var renderer = CreateRenderer();

        var content = renderer.RenderSurveyInvitation(
            "Daniel <Admin>",
            "Dates & places",
            "token + value",
            "en",
            "  Help choose our dates  ",
            "  **Choose carefully.**\r\n\r\n- Friday\r\n- Saturday\r\n\r\n[Details](https://example.com)\r\n\r\n![Poster](https://example.com/poster.png)\r\n<script>alert('x')</script>  ");

        content.Subject.Should().Be("Help choose our dates");
        content.HtmlBody.Should().Contain("Daniel &lt;Admin&gt;");
        content.HtmlBody.Should().Contain("<h2>Dates &amp; places</h2>");
        content.HtmlBody.Should().Contain("<p><strong>Choose carefully.</strong></p>");
        content.HtmlBody.Should().Contain("<ul>");
        content.HtmlBody.Should().Contain("<li>Friday</li>");
        content.HtmlBody.Should().Contain("<a href=\"https://example.com\">Details</a>");
        content.HtmlBody.Should().NotContain("<script>");
        content.HtmlBody.Should().NotContain("<img");
        content.HtmlBody.Should().NotContain("poster.png");
        content.HtmlBody.Should().Contain(
            "https://humans.example/Survey/Answer?t=token%20%2B%20value");
    }

    [HumansFact]
    public void SurveyInvitation_blank_custom_copy_retains_standard_localized_wording()
    {
        var renderer = CreateRenderer();

        var content = renderer.RenderSurveyInvitation(
            "Daniel", "Test Survey", "token", "en", " ", null);

        content.Subject.Should().Be("Please complete: Test Survey");
        content.HtmlBody.Should().Contain(
            "<p>You're invited to complete <strong>Test Survey</strong>.</p>");
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
            ["Email_SurveyInvitation_Subject"] = "Please complete: {0}",
            ["Email_SurveyInvitation_DefaultMessage"] = "You're invited to complete <strong>{0}</strong>.",
            ["Email_SurveyInvitation_Body"] =
                "<h2>{1}</h2><p>Hi {0},</p>{3}<p><a href=\"{2}\">Open the survey</a></p>",
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
