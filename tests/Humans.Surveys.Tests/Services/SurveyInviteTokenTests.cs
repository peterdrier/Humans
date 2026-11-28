using AwesomeAssertions;
using Humans.Surveys.Services;
using Microsoft.AspNetCore.DataProtection;

namespace Humans.Surveys.Tests.Services;

public class SurveyInviteTokenTests
{
    private static SurveyInviteTokenProvider CreateProvider()
        => new(DataProtectionProvider.Create("survey-tests"));

    [HumansFact]
    public void Resolve_ValidToken_ReturnsInvitationId()
    {
        var provider = CreateProvider();
        var id = Guid.NewGuid();

        provider.Resolve(provider.Create(id)).Should().Be((id, null));
    }

    [HumansFact]
    public void Resolve_ExpiredToken_ReturnsExpired()
    {
        var dataProtection = DataProtectionProvider.Create("survey-tests");
        var expired = dataProtection.CreateProtector("SurveyInvite").ToTimeLimitedDataProtector()
            .Protect(Guid.NewGuid().ToString(), DateTimeOffset.UtcNow.AddDays(-1));

        new SurveyInviteTokenProvider(dataProtection).Resolve(expired)
            .Should().Be(((Guid?)null, SurveyLinkFailure.Expired));
    }

    [HumansFact]
    public void Resolve_TamperedToken_ReturnsNull()
    {
        var provider = CreateProvider();
        var token = provider.Create(Guid.NewGuid());
        var tampered = token[..^2] + (token[^1] == 'A' ? "BB" : "AA");

        provider.Resolve(tampered).Failure.Should().Be(SurveyLinkFailure.Unreadable);
    }

    [HumansFact]
    public void Resolve_Garbage_ReturnsNull()
    {
        CreateProvider().Resolve("not-a-real-token").Failure.Should().Be(SurveyLinkFailure.Unreadable);
    }

    [HumansFact]
    public void Resolve_MissingToken_ReturnsNull()
    {
        // /Survey/Answer and /Survey/Answer/ThankYou are [AllowAnonymous]; a request with no ?t=
        // must read as a missing token, not a 500.
        var provider = CreateProvider();

        provider.Resolve(null!).Failure.Should().Be(SurveyLinkFailure.NoToken);
        provider.Resolve("").Failure.Should().Be(SurveyLinkFailure.NoToken);
        provider.Resolve("   ").Failure.Should().Be(SurveyLinkFailure.NoToken);
    }

    [HumansFact]
    public void PreviewToken_Resolved_ReturnsSurveyIdCulture()
    {
        var dataProtection = DataProtectionProvider.Create("survey-preview-tests");
        var previewProvider = new SurveyPreviewTokenProvider(dataProtection);
        var inviteProvider = new SurveyInviteTokenProvider(dataProtection);
        var surveyId = Guid.NewGuid();

        var token = previewProvider.Create(surveyId, "fr");

        previewProvider.Resolve(token).Should().Be(new SurveyPreviewLink(surveyId, "fr"));
        inviteProvider.Resolve(token).InvitationId.Should().BeNull();
        previewProvider.Resolve(inviteProvider.Create(Guid.NewGuid())).Should().BeNull();
    }
}
