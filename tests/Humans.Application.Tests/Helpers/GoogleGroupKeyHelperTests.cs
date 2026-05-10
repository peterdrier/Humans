using AwesomeAssertions;
using Humans.Application.Helpers;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Xunit;

namespace Humans.Application.Tests.Helpers;

public sealed class GoogleGroupKeyHelperTests
{
    [HumansFact]
    public void TryGetGroupKey_PrefersTeamEmail()
    {
        var resource = GroupResource(
            googleId: "group-id",
            url: "https://groups.google.com/a/nobodies.team/g/from-url");

        GoogleGroupKeyHelper.TryGetGroupKey(resource, "from-team@nobodies.team")
            .Should().Be("from-team@nobodies.team");
    }

    [HumansFact]
    public void TryGetGroupKey_DerivesEmailFromUrl()
    {
        var resource = GroupResource(
            googleId: "group-id",
            url: "https://groups.google.com/a/nobodies.team/g/from-url");

        GoogleGroupKeyHelper.TryGetGroupKey(resource)
            .Should().Be("from-url@nobodies.team");
    }

    [HumansFact]
    public void TryGetGroupKey_DerivesUrlPrefixWithConfiguredDomainFallback()
    {
        var resource = GroupResource(
            googleId: "numeric-group-id",
            url: "https://groups.google.com/g/from-url");

        GoogleGroupKeyHelper.TryGetGroupKey(resource, configuredDomain: "nobodies.team")
            .Should().Be("from-url@nobodies.team");
    }

    [HumansFact]
    public void TryGetGroupKey_FallsBackToEmailGoogleId()
    {
        var resource = GroupResource(googleId: "legacy@nobodies.team", url: null);

        GoogleGroupKeyHelper.TryGetGroupKey(resource)
            .Should().Be("legacy@nobodies.team");
    }

    private static GoogleResource GroupResource(string googleId, string? url) => new()
    {
        Id = Guid.NewGuid(),
        TeamId = Guid.NewGuid(),
        ResourceType = GoogleResourceType.Group,
        GoogleId = googleId,
        Name = "Group",
        Url = url,
        IsActive = true
    };
}
