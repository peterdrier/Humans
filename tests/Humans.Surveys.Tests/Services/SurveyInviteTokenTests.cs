using AwesomeAssertions;
using Humans.Surveys.Services;
using Microsoft.AspNetCore.DataProtection;
using Humans.Surveys.Contracts;

namespace Humans.Surveys.Tests.Services;

public class SurveyInviteTokenTests
{
    private static SurveyInviteTokenProvider CreateProvider()
        => new(DataProtectionProvider.Create("survey-tests"));

    [HumansFact]
    public void Resolve_round_trips_invitation_id()
    {
        var provider = CreateProvider();
        var id = Guid.NewGuid();

        provider.Resolve(provider.Create(id)).Should().Be(id);
    }

    [HumansFact]
    public void Resolve_returns_null_for_tampered_token()
    {
        var provider = CreateProvider();
        var token = provider.Create(Guid.NewGuid());
        var tampered = token[..^2] + (token[^1] == 'A' ? "BB" : "AA");

        provider.Resolve(tampered).Should().BeNull();
    }

    [HumansFact]
    public void Resolve_returns_null_for_garbage()
    {
        CreateProvider().Resolve("not-a-real-token").Should().BeNull();
    }

    [HumansFact]
    public void Preview_token_round_trips_survey_id_and_culture_and_is_distinct_from_invite_tokens()
    {
        var dataProtection = DataProtectionProvider.Create("survey-preview-tests");
        var previewProvider = new SurveyPreviewTokenProvider(dataProtection);
        var inviteProvider = new SurveyInviteTokenProvider(dataProtection);
        var surveyId = Guid.NewGuid();

        var token = previewProvider.Create(surveyId, "fr");

        previewProvider.Resolve(token).Should().Be(new SurveyPreviewLink(surveyId, "fr"));
        inviteProvider.Resolve(token).Should().BeNull();
        previewProvider.Resolve(inviteProvider.Create(Guid.NewGuid())).Should().BeNull();
    }
}
