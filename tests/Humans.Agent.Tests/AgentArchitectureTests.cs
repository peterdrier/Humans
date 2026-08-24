using AwesomeAssertions;
using Humans.Agent.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Agent.Tests;

/// <summary>
/// Architecture tests enforcing the section shape for Agent
/// (nobodies-collective/Humans#866, G5).
/// </summary>
/// <remarks>
/// Replaces <c>Humans.Application.Tests/Architecture/AgentArchitectureTests.cs</c>, whose three
/// tests all pinned the Application/Infrastructure split the section no longer has: they
/// asserted that <c>AgentService</c> sat in <c>Humans.Application</c> without EF Core, that the
/// five helpers sat in <c>Humans.Infrastructure.Services.Agent</c>, and that their interfaces
/// sat in <c>Humans.Application</c>. One assembly with one internal surface subsumes all three
/// (design §15 step 11).
/// </remarks>
public class AgentArchitectureTests
{
    [HumansFact]
    public void SectionRegistersTheConversationRetentionForwarder()
    {
        // The daily job that deletes expired agent conversations asks for this interface.
        // Lose the registration and the job stops running, so old conversations are kept
        // forever. The job itself is not in DI, so no start-up check would notice.
        var services = new ServiceCollection();
        new Section().Register(services, new ConfigurationBuilder().Build());

        services.Should().ContainSingle(d => d.ServiceType == typeof(IAgentConversationRetention));
    }
}
