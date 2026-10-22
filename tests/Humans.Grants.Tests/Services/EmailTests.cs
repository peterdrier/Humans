using AwesomeAssertions;
using Humans.Grants.Services;
using Humans.Grants.Tests.Infrastructure;
using NodaTime;

namespace Humans.Grants.Tests.Services;

/// <summary>The notice email: real resx copy, recipient culture, encoded member input.</summary>
public sealed class EmailTests
{
    [HumansTheory]
    [Xunit.InlineData("en", "You have a grant")]
    [Xunit.InlineData("es", "Tienes una concesión")]
    [Xunit.InlineData("ca", "Tens una concessió")]
    public void Issued_RendersInRecipientCulture_WithExpiryAndLink(string culture, string subjectStart)
    {
        var message = TestGrantsEmails.CreateReal().Notice(new GrantNoticeRequest(
            Guid.NewGuid(),
            "a@example.org", "Ana", GrantNoticeKind.Issued, "Build crew 2027", "a <cheap> ticket",
            new LocalDate(2027, 5, 1), GrantService.ProfileUrl, Culture: culture));

        message.Subject.Should().StartWith(subjectStart).And.EndWith("Build crew 2027");
        message.HtmlBody.Should().Contain("a &lt;cheap&gt; ticket")
            .And.Contain($"{TestGrantsEmails.BaseUrl}/Profile/Me")
            .And.Contain("2027");
        message.Culture.Should().Be(culture);
    }

    [HumansFact]
    public void Revoked_CarriesTheReason()
    {
        var message = TestGrantsEmails.CreateReal().Notice(new GrantNoticeRequest(
            Guid.NewGuid(),
            "a@example.org", null, GrantNoticeKind.Revoked, "Build crew 2027", "a ticket", null,
            GrantService.ProfileUrl, "You left the crew.", "en"));

        message.Subject.Should().Be("Grant revoked: Build crew 2027");
        message.HtmlBody.Should().Contain("You left the crew.").And.StartWith("<p>Hi,</p>");
    }
}
