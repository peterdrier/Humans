using AwesomeAssertions;
using Humans.Infrastructure.Services.Surveys;
using Microsoft.AspNetCore.DataProtection;

namespace Humans.Application.Tests.Surveys;

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
}
