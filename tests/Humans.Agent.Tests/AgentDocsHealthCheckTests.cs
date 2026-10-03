using AwesomeAssertions;
using Humans.Agent.Contracts;
using Humans.Agent.Health;
using Humans.Base.Interfaces;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Octokit;
using Xunit;

namespace Humans.Agent.Tests;

/// <summary>
/// The probe must name files that exist in the repo it fetches from: a canary that was
/// deleted reports Degraded on every call while the agent is enabled.
/// </summary>
public class AgentDocsHealthCheckTests
{
    [HumansFact]
    public async Task Healthy_when_both_canaries_exist_in_this_repo()
    {
        var source = Substitute.For<IGuideContentSource>();
        source.GetMarkdownAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => ReadFromRepo(call.ArgAt<string>(0), call.ArgAt<string>(1)));

        var result = await Check(source);

        result.Status.Should().Be(HealthStatus.Healthy, result.Description);
    }

    [HumansFact]
    public async Task Degraded_when_the_section_guide_canary_is_missing()
    {
        var source = Substitute.For<IGuideContentSource>();
        source.GetMarkdownAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Throws(new NotFoundException("missing", System.Net.HttpStatusCode.NotFound));

        var result = await Check(source);

        result.Status.Should().Be(HealthStatus.Degraded);
        result.Description.Should().Contain("src/Sections/Humans.Agent/Docs/Agent.md");
    }

    private static Task<HealthCheckResult> Check(IGuideContentSource source)
    {
        var agent = Substitute.For<IAgentAvailability>();
        agent.IsEnabled.Returns(true);
        return new AgentDocsHealthCheck(agent, source, NullLogger<AgentDocsHealthCheck>.Instance)
            .CheckHealthAsync(new HealthCheckContext(), Xunit.TestContext.Current.CancellationToken);
    }

    /// <summary>Serves the probe from this checkout, throwing as GitHub does on a missing file.</summary>
    private static Task<string> ReadFromRepo(string folder, string stem)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Humans.slnx")))
            dir = dir.Parent;
        var path = Path.Combine(dir!.FullName, folder, stem + ".md");
        return File.Exists(path)
            ? Task.FromResult(File.ReadAllText(path))
            : throw new NotFoundException(path, System.Net.HttpStatusCode.NotFound);
    }
}
