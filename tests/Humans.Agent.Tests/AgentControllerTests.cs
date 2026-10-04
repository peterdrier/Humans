using System.Security.Claims;
using AwesomeAssertions;
using Humans.Agent.Controllers;
using Humans.Agent.Domain;
using Humans.Agent.Models;
using Humans.Agent.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NSubstitute;

namespace Humans.Agent.Tests;

public class AgentControllerTests
{
    [HumansFact]
    public async Task Ask_rejects_an_over_cap_message_with_400_before_any_work()
    {
        var agent = Substitute.For<IAgentService>();
        var controller = MakeController(agent, enabled: true);

        await controller.Ask(
            new AgentAskRequest { Message = new string('x', AgentAskRequest.MaxMessageLength + 1) },
            Xunit.TestContext.Current.CancellationToken);

        controller.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        agent.DidNotReceiveWithAnyArgs().AskAsync(default!, default);
    }

    [HumansFact]
    public async Task Ask_rejects_a_null_message_with_400()
    {
        // STJ binds {"message":null} despite the non-nullable declaration.
        var agent = Substitute.For<IAgentService>();
        var controller = MakeController(agent, enabled: true);

        await controller.Ask(
            new AgentAskRequest { Message = null! },
            Xunit.TestContext.Current.CancellationToken);

        controller.Response.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        agent.DidNotReceiveWithAnyArgs().AskAsync(default!, default);
    }

    [HumansFact]
    public async Task Ask_accepts_a_message_at_exactly_the_cap()
    {
        // Enabled=false stops the turn at 503 right after the length guard,
        // proving an at-cap message passes the guard itself.
        var agent = Substitute.For<IAgentService>();
        var controller = MakeController(agent, enabled: false);

        await controller.Ask(
            new AgentAskRequest { Message = new string('x', AgentAskRequest.MaxMessageLength) },
            Xunit.TestContext.Current.CancellationToken);

        controller.Response.StatusCode.Should().Be(StatusCodes.Status503ServiceUnavailable);
    }

    [HumansFact]
    public async Task Ask_answers_403_before_streaming_when_the_conversation_is_someone_elses()
    {
        var agent = Substitute.For<IAgentService>();
        agent.AskAsync(Arg.Any<AgentTurnRequest>(), Arg.Any<CancellationToken>())
            .Returns(Foreign());
        var controller = MakeController(agent, enabled: true);

        await controller.Ask(
            new AgentAskRequest { Message = "hi", ConversationId = Guid.NewGuid() },
            Xunit.TestContext.Current.CancellationToken);

        controller.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        controller.Response.ContentType.Should().BeNull("no event stream was opened");

        static async IAsyncEnumerable<AgentTurnToken> Foreign()
        {
            await Task.Yield();
            throw new UnauthorizedAccessException();
#pragma warning disable CS0162 // an iterator needs a yield to compile
            yield break;
#pragma warning restore CS0162
        }
    }

    private static AgentController MakeController(IAgentService agent, bool enabled)
    {
        var userId = Guid.NewGuid();
        var users = Substitute.For<IUserServiceRead>();
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfo.Create(
                new User { Id = userId, DisplayName = "T", PreferredLanguage = "en" },
                [], [], [], profile: null, [])));

        var settings = Substitute.For<IAgentSettingsService>();
        settings.Current.Returns(new AgentSettingsDto(
            Enabled: enabled, Model: "m", PreloadConfig: AgentPreloadConfig.Tier1,
            DailyMessageCap: 10, HourlyMessageCap: 5, DailyTokenCap: 50_000,
            RetentionDays: 30, UpdatedAt: Instant.MinValue));

        var auth = Substitute.For<IAuthorizationService>();
        auth.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<IEnumerable<IAuthorizationRequirement>>())
            .Returns(AuthorizationResult.Success());

        var controller = new AgentController(agent, auth, settings, users, users)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test")),
                },
            },
        };
        return controller;
    }
}
