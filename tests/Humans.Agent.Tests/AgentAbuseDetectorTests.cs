using AwesomeAssertions;
using Humans.Agent.Services;
using Xunit;
using Humans.Agent.Contracts;
// ReSharper disable StringLiteralTypo

namespace Humans.Agent.Tests;

public class AgentAbuseDetectorTests
{
    [HumansTheory]
    [InlineData("how do I join a team?", false)]
    [InlineData("what is my consent status?", false)]
    [InlineData("I want to hurt myself", true)]
    [InlineData("quiero hacerme daño", true)]
    public void Flags_obvious_self_harm_phrases(string message, bool expectedFlagged)
    {
        var detector = new AgentAbuseDetector();
        detector.IsFlagged(message, out var reason).Should().Be(expectedFlagged);
        if (expectedFlagged) reason.Should().NotBeNullOrEmpty();
    }
}
