using AwesomeAssertions;
using Humans.Application.Constants;
using Humans.Application.Interfaces;
using Humans.Application.Models;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Agent;

public class AgentToolDispatcherTests
{
    [Fact]
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

    [Fact]
    public async Task RouteToFeedback_calls_IFeedbackService_and_returns_feedback_url()
    {
        var feedback = Substitute.For<IFeedbackService>();
        feedback.SubmitFromAgentAsync(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new FeedbackHandoffResult(Guid.Parse("11111111-1111-1111-1111-111111111111"), "/Feedback/11111111-1111-1111-1111-111111111111"));
        var dispatcher = MakeDispatcher(feedback: feedback);

        var result = await dispatcher.DispatchAsync(
            new AnthropicToolCall("t1", AgentToolNames.RouteToFeedback, """{"summary":"can't answer","topic":"camps"}"""),
            userId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
            conversationId: Guid.Parse("33333333-3333-3333-3333-333333333333"),
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("/Feedback/11111111");
        await feedback.Received(1).SubmitFromAgentAsync(
            Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            "can't answer", "camps", Arg.Any<CancellationToken>());
    }

    private static Humans.Infrastructure.Services.Agent.AgentToolDispatcher MakeDispatcher(IFeedbackService? feedback = null)
    {
        var env = new TestHostEnvironment();
        var sections = new Humans.Infrastructure.Services.Preload.AgentSectionDocReader(env);
        var features = new Humans.Infrastructure.Services.Preload.AgentFeatureSpecReader(env);
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<Humans.Infrastructure.Services.Agent.AgentToolDispatcher>.Instance;
        return new Humans.Infrastructure.Services.Agent.AgentToolDispatcher(sections, features, feedback ?? Substitute.For<IFeedbackService>(), logger);
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
