using AwesomeAssertions;
using Humans.Backdoor.Controllers;
using Humans.Backdoor.Filters;
using Humans.Feedback.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using System.Security.Claims;
using Xunit;

namespace Humans.Backdoor.Tests.Controllers;

/// <summary>
/// The machine surface at <c>/api/backdoor/feedback</c>. Mocks <see cref="IFeedbackTriage"/>
/// and drives the controller directly (no HTTP roundtrip); the auth filter is covered by
/// <see cref="BackdoorApiKeyAuthFilterTests"/>.
/// </summary>
/// <remarks>
/// The invariant these pin is attribution: every write here must reach Feedback carrying the
/// human the presented key was issued to, not <c>null</c> and not somebody else. Backdoor
/// exists to close exactly that hole, and until now nothing tested it on this controller.
/// </remarks>
public class BackdoorFeedbackControllerTests
{
    private static readonly Guid KeyOwnerId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly IFeedbackTriage _feedback = Substitute.For<IFeedbackTriage>();
    private readonly IUserServiceRead _users = Substitute.For<IUserServiceRead>();
    private readonly BackdoorFeedbackController _sut;

    public BackdoorFeedbackControllerTests()
    {
        _feedback.UpdateStatusAsync(Arg.Any<Guid>(), Arg.Any<FeedbackStatus>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(FeedbackMutationResult.Success);
        _feedback.UpdateAssignmentAsync(Arg.Any<Guid>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(FeedbackMutationResult.Success);
        _feedback.SetGitHubIssueNumberAsync(Arg.Any<Guid>(), Arg.Any<int?>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(FeedbackMutationResult.Success);
        _sut = new BackdoorFeedbackController(
            _feedback, _users, NullLogger<BackdoorFeedbackController>.Instance)
        {
            // What BackdoorApiKeyAuthFilter installs once it has resolved the key.
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext
                {
                    User = new ClaimsPrincipal(new ClaimsIdentity(
                        [new Claim(ClaimTypes.NameIdentifier, KeyOwnerId.ToString())],
                        BackdoorApiKeyAuthFilter.AuthenticationScheme)),
                },
            },
        };
    }

    [HumansFact]
    public async Task AbandonedReadRequests_CancelFeedbackLoads()
    {
        var id = Guid.NewGuid();
        using var request = new CancellationTokenSource();
        _sut.HttpContext.RequestAborted = request.Token;
        _feedback.GetFeedbackListAsync(Arg.Any<FeedbackStatus?>(), Arg.Any<FeedbackCategory?>(),
                limit: Arg.Any<int>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                call.Arg<CancellationToken>().ThrowIfCancellationRequested();
                return Task.FromResult<IReadOnlyList<FeedbackReportInfo>>([]);
            });
        _feedback.GetFeedbackByIdAsync(id, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return Task.FromResult<FeedbackReportInfo?>(null);
        });
        (await _sut.List(null, null)).Should().BeOfType<OkObjectResult>();
        (await _sut.Get(id)).Should().BeOfType<NotFoundResult>();
        (await _sut.GetMessages(id)).Should().BeOfType<NotFoundResult>();
        await request.CancelAsync();
        Func<Task<IActionResult>>[] reads =
        [() => _sut.List(null, null), () => _sut.Get(id), () => _sut.GetMessages(id)];
        foreach (var read in reads)
        {
            Func<Task> operation = async () => await read();
            await operation.Should().ThrowAsync<OperationCanceledException>();
        }
    }

    // ==========================================================================
    // Every write records the key owner as the actor
    // ==========================================================================

    [HumansFact]
    public async Task PostMessage_records_the_key_owner_as_the_sender()
    {
        var id = Guid.NewGuid();
        _feedback.PostMessageAsync(id, KeyOwnerId, "Looking into it", Arg.Any<CancellationToken>())
            .Returns(new FeedbackMutationResult(true, Message: new FeedbackMessageInfo(
                Guid.NewGuid(), id, KeyOwnerId, "Admin", "Looking into it",
                Instant.FromUtc(2026, 9, 3, 10, 0))));

        var result = await _sut.PostMessage(id, new PostFeedbackMessageModel { Content = "Looking into it" });

        result.Should().BeOfType<OkObjectResult>();
        await _feedback.Received(1).PostMessageAsync(
            id, KeyOwnerId, "Looking into it", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task UpdateStatus_records_the_key_owner_as_actor()
    {
        var id = Guid.NewGuid();

        await _sut.UpdateStatus(id, new UpdateFeedbackStatusModel { Status = FeedbackStatus.Resolved });

        await _feedback.Received(1).UpdateStatusAsync(
            id, FeedbackStatus.Resolved, KeyOwnerId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task UpdateAssignment_records_the_key_owner_as_actor()
    {
        var id = Guid.NewGuid();
        var assignee = Guid.NewGuid();
        var team = Guid.NewGuid();

        await _sut.UpdateAssignment(id, new UpdateFeedbackAssignmentModel
        {
            AssignedToUserId = assignee,
            AssignedToTeamId = team,
        });

        await _feedback.Received(1).UpdateAssignmentAsync(
            id, assignee, team, KeyOwnerId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SetGitHubIssue_records_the_key_owner_as_actor()
    {
        var id = Guid.NewGuid();

        await _sut.SetGitHubIssue(id, new SetFeedbackGitHubIssueModel { IssueNumber = 1234 });

        await _feedback.Received(1).SetGitHubIssueNumberAsync(
            id, 1234, KeyOwnerId, Arg.Any<CancellationToken>());
    }

    // ==========================================================================
    // A report that is gone is a 404, not a 500
    // ==========================================================================

    [HumansFact]
    public async Task Get_returns_NotFound_for_a_missing_report()
    {
        var id = Guid.NewGuid();
        _feedback.GetFeedbackByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<FeedbackReportInfo?>(null));

        var result = await _sut.Get(id);

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task GetMessages_returns_NotFound_for_a_missing_report()
    {
        var id = Guid.NewGuid();
        _feedback.GetFeedbackByIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<FeedbackReportInfo?>(null));

        var result = await _sut.GetMessages(id);

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task PostMessage_returns_NotFound_when_the_report_is_gone()
    {
        var id = Guid.NewGuid();
        _feedback.PostMessageAsync(id, KeyOwnerId, "hello", Arg.Any<CancellationToken>())
            .Returns(FeedbackMutationResult.Missing);

        var result = await _sut.PostMessage(id, new PostFeedbackMessageModel { Content = "hello" });

        result.Should().BeOfType<NotFoundResult>();
    }

    [HumansFact]
    public async Task UpdateStatus_returns_NotFound_when_the_report_is_gone()
    {
        var id = Guid.NewGuid();
        _feedback.UpdateStatusAsync(id, FeedbackStatus.Resolved, KeyOwnerId, Arg.Any<CancellationToken>())
            .Returns(FeedbackMutationResult.Missing);

        var result = await _sut.UpdateStatus(id, new UpdateFeedbackStatusModel { Status = FeedbackStatus.Resolved });

        result.Should().BeOfType<NotFoundResult>();
    }

    /// <summary>
    /// A rejected move is not a missing report — a refusal is 422 so the caller can tell refused
    /// from missing rather than stop retrying something that was merely refused.
    /// </summary>
    [HumansFact]
    public async Task UpdateStatus_returns_422_when_the_service_rejects_the_move()
    {
        var id = Guid.NewGuid();
        _feedback.UpdateStatusAsync(id, FeedbackStatus.Resolved, KeyOwnerId, Arg.Any<CancellationToken>())
            .Returns(new FeedbackMutationResult(true, Rejection: "Already resolved"));

        var result = await _sut.UpdateStatus(id, new UpdateFeedbackStatusModel { Status = FeedbackStatus.Resolved });

        var body = result.Should().BeOfType<UnprocessableEntityObjectResult>().Subject;
        body.Value.Should().BeEquivalentTo(new { error = "Already resolved" });
    }

    [HumansTheory]
    [Xunit.InlineData("message")]
    [Xunit.InlineData("status")]
    [Xunit.InlineData("assignment")]
    [Xunit.InlineData("github")]
    public async Task DependencyInvalidOperation_Remains500EvenWhenMessageContainsNotFound(string action)
    {
        var id = Guid.NewGuid();
        var failure = Task.FromException<FeedbackMutationResult>(
            new InvalidOperationException("Provider not found; database unavailable"));
        _feedback.PostMessageAsync(id, KeyOwnerId, "hello", Arg.Any<CancellationToken>()).Returns(failure);
        _feedback.UpdateStatusAsync(id, FeedbackStatus.Resolved, KeyOwnerId, Arg.Any<CancellationToken>()).Returns(failure);
        _feedback.UpdateAssignmentAsync(id, null, null, KeyOwnerId, Arg.Any<CancellationToken>()).Returns(failure);
        _feedback.SetGitHubIssueNumberAsync(id, 12, KeyOwnerId, Arg.Any<CancellationToken>()).Returns(failure);

        var result = action switch
        {
            "message" => await _sut.PostMessage(id, new PostFeedbackMessageModel { Content = "hello" }),
            "status" => await _sut.UpdateStatus(id, new UpdateFeedbackStatusModel { Status = FeedbackStatus.Resolved }),
            "assignment" => await _sut.UpdateAssignment(id, new UpdateFeedbackAssignmentModel()),
            _ => await _sut.SetGitHubIssue(id, new SetFeedbackGitHubIssueModel { IssueNumber = 12 })
        };

        var error = result.Should().BeOfType<ObjectResult>().Subject;
        error.StatusCode.Should().Be(500);
        System.Text.Json.JsonSerializer.Serialize(error.Value).Should().NotContain("Provider not found");
    }

    // ==========================================================================
    // List
    // ==========================================================================

    [HumansFact]
    public async Task List_passes_the_status_and_category_filters_through()
    {
        _feedback.GetFeedbackListAsync(
                Arg.Any<FeedbackStatus?>(), Arg.Any<FeedbackCategory?>(), limit: Arg.Any<int>())
            .Returns([]);

        await _sut.List(FeedbackStatus.Open, FeedbackCategory.Bug, limit: 25);

        await _feedback.Received(1).GetFeedbackListAsync(
            FeedbackStatus.Open, FeedbackCategory.Bug, limit: 25);
    }

    /// <summary>
    /// The raw query value would reach a SQL <c>LIMIT</c> — the repository's list query ends
    /// in <c>.Take(limit)</c> over rows carrying their whole message thread. The controller
    /// clamps, like the section's other list endpoints.
    /// </summary>
    [HumansTheory]
    [InlineData(-1, 1)]
    [InlineData(0, 1)]
    [InlineData(50, 50)]
    [InlineData(5000, 1000)]
    public async Task List_clamps_the_limit(int requested, int expected)
    {
        _feedback.GetFeedbackListAsync(
                Arg.Any<FeedbackStatus?>(), Arg.Any<FeedbackCategory?>(), limit: Arg.Any<int>())
            .Returns([]);

        await _sut.List(status: null, category: null, limit: requested);

        await _feedback.Received(1).GetFeedbackListAsync(null, null, limit: expected);
    }
}
