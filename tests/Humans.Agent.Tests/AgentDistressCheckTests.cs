using AwesomeAssertions;
using Humans.Agent.Services;
using Xunit;
// ReSharper disable StringLiteralTypo

namespace Humans.Agent.Tests;

public class AgentDistressCheckTests
{
    [HumansTheory]
    [InlineData("how do I join a team?", false)]
    [InlineData("what is my consent status?", false)]
    [InlineData("I want to hurt myself", true)]
    [InlineData("quiero hacerme daño", true)]
    public void Matches_Question_DetectsSelfHarmPhrases(string question, bool expected)
    {
        new AgentDistressCheck().Matches(question).Should().Be(expected);
    }
}
