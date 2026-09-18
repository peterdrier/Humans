using AwesomeAssertions;
using Humans.Base.Interfaces;
using NSubstitute;
using Xunit;

namespace Humans.Events.Tests;

public sealed class SectionUserPartTests
{
    [HumansFact]
    public async Task PartsAsync_ContributesEventsCardForProfileTarget()
    {
        IUserPart contribution = new Section();
        var targetUserId = Guid.NewGuid();

        var parts = await contribution.PartsAsync(
            Substitute.For<IServiceProvider>(),
            new System.Security.Claims.ClaimsPrincipal(),
            targetUserId);

        parts.Should().ContainSingle().Which.Should().Be(new UserPart("EventsCard"));
    }
}
