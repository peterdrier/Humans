using AwesomeAssertions;
using Humans.Application.Tests.Infrastructure;
using Humans.Infrastructure.Configuration;
using Humans.Infrastructure.Services;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Humans.Application.Tests.Camps;

/// <summary>
/// Guards that <see cref="EmailRenderer.RenderBarrioShiftObligationReminder"/> absolutizes
/// the (deliberately relative) sign-up link against <c>EmailSettings.BaseUrl</c> — mail
/// clients can't follow site-relative hrefs, so the CTA must be a full URL.
/// </summary>
public sealed class BarrioShiftReminderRendererTests
{
    private const string BaseUrl = "https://humans.example.test";

    // Mirror of the real Email_BarrioShiftReminder_Body resx href so the test exercises the
    // same {4}-into-href substitution the production template uses.
    private const string BodyTemplate =
        "<p>Short on shifts.</p><p><a href=\"{4}\">Sign up for {1} shifts</a></p>";

    // Mirror of the real Subject resx: "{0}" = barrio name, "{1}" = function name.
    private const string SubjectTemplate = "{0} owes shifts for {1}";

    private static EmailRenderer NewRenderer()
    {
        var localizer = Substitute.For<IStringLocalizer>();
        // Default: echo the key back (matches resx-miss fallback); body key returns the real href template.
        localizer[Arg.Any<string>()]
            .Returns(ci => new LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        localizer["Email_BarrioShiftReminder_Body"]
            .Returns(new LocalizedString("Email_BarrioShiftReminder_Body", BodyTemplate));
        localizer["Email_BarrioShiftReminder_Subject"]
            .Returns(new LocalizedString("Email_BarrioShiftReminder_Subject", SubjectTemplate));

        var factory = Substitute.For<IStringLocalizerFactory>();
        factory.Create(Arg.Any<string>(), Arg.Any<string>()).Returns(localizer);

        var settings = Options.Create(new EmailSettings { BaseUrl = BaseUrl });
        return new EmailRenderer(settings, factory, NullLogger<EmailRenderer>.Instance);
    }

    [HumansFact]
    public void RelativeLink_IsAbsolutizedAgainstBaseUrl()
    {
        var content = NewRenderer().RenderBarrioShiftObligationReminder(
            "Rcpt", "Norg", "Bar", doneCount: 1, requiredCount: 5,
            link: "/Teams/norg-bar/Shifts", culture: null);

        content.HtmlBody.Should().Contain($"href=\"{BaseUrl}/Teams/norg-bar/Shifts\"");
        content.HtmlBody.Should().NotContain("href=\"/Teams/");
    }

    [HumansFact]
    public void AlreadyAbsoluteLink_IsLeftIntact()
    {
        var content = NewRenderer().RenderBarrioShiftObligationReminder(
            "Rcpt", "Norg", "Bar", doneCount: 1, requiredCount: 5,
            link: $"{BaseUrl}/Teams/norg-bar/Shifts", culture: null);

        content.HtmlBody.Should().Contain($"href=\"{BaseUrl}/Teams/norg-bar/Shifts\"");
        content.HtmlBody.Should().NotContain($"{BaseUrl}{BaseUrl}");
    }

    [HumansFact]
    public void Subject_IsNotHtmlEncoded()
    {
        // The subject is a plain-text mail header; '&' must survive verbatim, not as "&amp;".
        var content = NewRenderer().RenderBarrioShiftObligationReminder(
            "Rcpt", "Drinks & Snacks", "Bar & Grill", doneCount: 1, requiredCount: 5,
            link: "/Teams/x/Shifts", culture: null);

        content.Subject.Should().Be("Drinks & Snacks owes shifts for Bar & Grill");
        content.Subject.Should().NotContain("&amp;");
    }
}
