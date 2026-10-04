using System.Text.Json;
using AwesomeAssertions;
using Humans.Agent.Contracts;
using Humans.Backdoor.Controllers;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using NSubstitute;

namespace Humans.Backdoor.Tests.Controllers;

public sealed class BackdoorAgentControllerTests
{
    [HumansTheory]
    [Xunit.InlineData(10, "😀", "😀extra")]
    [Xunit.InlineData(199, "x", "x")]
    [Xunit.InlineData(199, "😀", "")]
    [Xunit.InlineData(198, "😀", "😀")]
    public async Task List_bounds_the_latest_user_preview_without_splitting_unicode(
        int prefixLength, string boundary, string expectedTail)
    {
        var agent = Substitute.For<IAgentTranscriptRead>();
        var users = Substitute.For<IUserServiceRead>();
        users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ValueTask.FromResult<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo>()));
        var conversationId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var now = Instant.FromUtc(2026, 10, 4, 0, 0);
        var prefix = new string('x', prefixLength);
        var content = prefix + boundary + "extra";
        var conversation = new AgentConversationTranscriptSnapshot(
            conversationId, userId, "es", now, now + Duration.FromSeconds(2), 3,
            [Message(AgentRole.User, "Earlier", now),
             Message(AgentRole.User, content, now + Duration.FromSeconds(1)),
             Message(AgentRole.Assistant, "Later assistant", now + Duration.FromSeconds(2))]);
        agent.ListAllConversationsForAdminWithMessagesAsync(
                false, false, null, 50, 0, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AgentConversationTranscriptSnapshot>>([conversation]));
        var controller = new BackdoorAgentController(agent, users);

        var result = await controller.List(ct: Xunit.TestContext.Current.CancellationToken);

        var value = result.Should().BeOfType<OkObjectResult>().Which.Value;
        var row = JsonSerializer.SerializeToElement(value).EnumerateArray().Single();
        row.GetProperty("LastUserMessagePreview").GetString().Should().Be(prefix + expectedTail);
        conversation.Messages[1].Content.Should().Be(content);

        AgentMessageSnapshot Message(AgentRole role, string text, Instant createdAt) =>
            new(Guid.NewGuid(), conversationId, role, text, createdAt, 0, 0, 0, "test", 0, [], null, null);
    }
}
