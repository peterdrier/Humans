using AwesomeAssertions;
using Humans.Application.Constants;
using Humans.Application.Models;
using Xunit;

namespace Humans.Application.Tests.Agent;

public class AgentToolDispatcherTests
{
    [HumansFact]
    public async Task Unknown_tool_name_returns_error_result()
    {
        var dispatcher = MakeDispatcher();
        var result = await dispatcher.DispatchAsync(
            new AnthropicToolCall("t1", "delete_users", "{}"),
            userId: Guid.NewGuid(),
            conversationId: Guid.NewGuid(),
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("Unknown tool");
    }

    [HumansFact]
    public async Task RouteToIssue_returns_proposal_marker_without_creating_anything()
    {
        var dispatcher = MakeDispatcher();

        var result = await dispatcher.DispatchAsync(
            new AnthropicToolCall("t1", AgentToolNames.RouteToIssue,
                """{"title":"Calendar feature","category":"Feature","description":"User asked about calendar; not implemented yet."}"""),
            userId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
            conversationId: Guid.Parse("33333333-3333-3333-3333-333333333333"),
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("Proposal queued");
    }

    private static Humans.Infrastructure.Services.Agent.AgentToolDispatcher MakeDispatcher()
    {
        var env = new TestHostEnvironment();
        var sections = new Humans.Infrastructure.Services.Preload.AgentSectionDocReader(env);
        var features = new Humans.Infrastructure.Services.Preload.AgentFeatureSpecReader(env);
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<Humans.Infrastructure.Services.Agent.AgentToolDispatcher>.Instance;
        return new Humans.Infrastructure.Services.Agent.AgentToolDispatcher(sections, features, logger);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "docs", "sections")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }

    private sealed class TestHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Test";
        public string ApplicationName { get; set; } = "Humans.Application.Tests";
        public string ContentRootPath { get; set; } = RepoRoot();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.PhysicalFileProvider(RepoRoot());
    }
}
