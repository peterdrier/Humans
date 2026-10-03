using AwesomeAssertions;
using Humans.Agent.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Humans.Agent.Tests;

/// <summary>
/// Architecture tests enforcing the section shape for Agent
/// (nobodies-collective/Humans#866).
/// </summary>
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
