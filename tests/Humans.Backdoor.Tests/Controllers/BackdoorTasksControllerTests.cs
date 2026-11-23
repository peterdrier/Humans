using System.Security.Claims;
using System.Text.Json;
using AwesomeAssertions;
using Humans.Backdoor.Controllers;
using Humans.Backdoor.Filters;
using Humans.Base.Authorization;
using Humans.Base.Enums;
using Humans.Permissions.Contracts;
using Humans.Tasks.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Backdoor.Tests.Controllers;

/// <summary>
/// <see cref="BackdoorTasksController"/>, the bulk load at <c>/api/backdoor/tasks/bulk</c>. Tasks' rules are covered in
/// its own tests; this is the check on each team before Tasks is called, and the wire shape.
/// </summary>
public sealed class BackdoorTasksControllerTests
{
    private static readonly Guid KeyOwnerId = Guid.NewGuid();
    private static readonly Guid Production = Guid.NewGuid();
    private static readonly Guid Art = Guid.NewGuid();

    private readonly ITasksWrite _tasks = Substitute.For<ITasksWrite>();
    private readonly IPermissionService _permissions = Substitute.For<IPermissionService>();
    private readonly ITeamService _teams = Substitute.For<ITeamService>();
    private readonly BackdoorTasksController _sut;

    public BackdoorTasksControllerTests()
    {
        _teams.GetTeamBySlugAsync("production", Arg.Any<CancellationToken>()).Returns(Team(Production, "production"));
        _teams.GetTeamBySlugAsync("art", Arg.Any<CancellationToken>()).Returns(Team(Art, "art"));
        _teams.GetTeamAsync(Art, Arg.Any<CancellationToken>()).Returns(Team(Art, "art"));
        _permissions.HasAsync(KeyOwnerId, TasksPermissions.Manage, new Owner(TeamOwner.Kind, Production), Arg.Any<CancellationToken>())
            .Returns(true);
        _permissions.HasAsync(KeyOwnerId, TasksPermissions.Manage, new Owner(TeamOwner.Kind, Art), Arg.Any<CancellationToken>())
            .Returns(false);
        _tasks.BulkLoadAsync(Arg.Any<BulkLoadRequest>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => new BulkLoadResult(
                [], [.. call.Arg<BulkLoadRequest>().Tasks.Select(t => new BulkLoadedTask(t.Key, Guid.NewGuid()))], call.Arg<BulkLoadRequest>().DryRun));

        _sut = new BackdoorTasksController(_tasks, _permissions, _teams, Substitute.For<IUserService>(), NullLogger<BackdoorTasksController>.Instance)
        {
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

    private static TeamInfo Team(Guid id, string slug) => new(
        id, slug, null, slug, true, false, SystemTeamType.None, false, false, false, false, Instant.FromUtc(2026, 1, 1, 0, 0), []);

    private static ApiBulkTasksModel Payload(bool dryRun = false, params string[] teams) => new()
    {
        DryRun = dryRun,
        Tasks = [.. teams.Select((t, i) => new ApiBulkTaskModel { Key = $"k{i}", Team = t, Title = $"Task {i}" })],
    };

    [HumansFact]
    public async Task Bulk_LoadsWhenTheKeyOwnerManagesEveryTeam()
    {
        var result = await _sut.Bulk(Payload(false, "production", "production"));

        result.Should().BeOfType<OkObjectResult>();
        await _tasks.Received(1).BulkLoadAsync(
            Arg.Is<BulkLoadRequest>(r => r.Tasks.Count == 2 && !r.DryRun), KeyOwnerId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Bulk_OneTeamWithoutManageRefusesTheWholePayloadBeforeTasksSeesIt()
    {
        var result = await _sut.Bulk(Payload(false, "production", "art", "production"));

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        await _tasks.DidNotReceiveWithAnyArgs().BulkLoadAsync(default!, default, default);
    }

    [HumansFact]
    public async Task Bulk_ADryRunNeedsTheSameRights()
    {
        var result = await _sut.Bulk(Payload(true, "art"));

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        await _tasks.DidNotReceiveWithAnyArgs().BulkLoadAsync(default!, default, default);
    }

    [HumansFact]
    public async Task Bulk_ATeamIsFoundByIdToo()
    {
        var result = await _sut.Bulk(Payload(false, Art.ToString()));

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [HumansFact]
    public async Task Bulk_AnUnknownTeamIsAnsweredExactlyAsATeamTheOwnerCannotManage()
    {
        var unknown = await _sut.Bulk(Payload(false, "nowhere"));
        var unmanaged = await _sut.Bulk(Payload(false, "art"));

        var unknownBody = unknown.Should().BeOfType<ObjectResult>().Which;
        var unmanagedBody = unmanaged.Should().BeOfType<ObjectResult>().Which;
        unknownBody.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        unmanagedBody.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        JsonSerializer.Serialize(unknownBody.Value).Replace("nowhere", "art", StringComparison.Ordinal)
            .Should().Be(JsonSerializer.Serialize(unmanagedBody.Value));
        await _tasks.DidNotReceiveWithAnyArgs().BulkLoadAsync(default!, default, default);
    }

    [HumansFact]
    public async Task Bulk_AnswersProblemsAs422()
    {
        _tasks.BulkLoadAsync(Arg.Any<BulkLoadRequest>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new BulkLoadResult([new BulkLoadProblem(0, "k0", "title", "Give the task a title.")], [], DryRun: false));

        var result = await _sut.Bulk(Payload(false, "production"));

        result.Should().BeOfType<UnprocessableEntityObjectResult>();
    }

    [HumansFact]
    public async Task Bulk_AnEmptyOrNullEntryNeverReachesTasksAsAnException()
    {
        var model = Payload(false, "production");
        model.Tasks.Add(null!);

        var result = await _sut.Bulk(model);

        result.Should().BeOfType<BadRequestObjectResult>();
        await _tasks.DidNotReceiveWithAnyArgs().BulkLoadAsync(default!, default, default);
    }

    [HumansFact]
    public void Wire_ReadsTheDocumentedShape()
    {
        const string json = """
            {
              "dryRun": true,
              "tasks": [
                { "key": "bar", "team": "production", "title": "Build the bar", "eventId": "11111111-1111-1111-1111-111111111111",
                  "start": { "day": -14 }, "due": { "relativeDate": "2027-03-01" }, "priority": "Critical", "slotCount": 3 },
                { "key": "sign", "team": "art", "title": "Paint the sign", "due": { "fixed": "2027-02-01" }, "dependsOn": ["bar"] }
              ]
            }
            """;

        var request = JsonSerializer.Deserialize<ApiBulkTasksModel>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!.ToRequest();

        request.DryRun.Should().BeTrue();
        request.Tasks[0].Priority.Should().Be(SlotPriority.Critical);
        request.Tasks[0].SlotCount.Should().Be(3);
        request.Tasks[0].Start.Should().Be(new BulkLoadDate(Day: -14));
        request.Tasks[0].Due.Should().Be(new BulkLoadDate(RelativeDate: new LocalDate(2027, 3, 1)));
        request.Tasks[1].Due.Should().Be(new BulkLoadDate(Fixed: new LocalDate(2027, 2, 1)));
        request.Tasks[1].DependsOn.Should().Equal("bar");
        request.Tasks[1].Priority.Should().Be(SlotPriority.Important);
        request.Tasks[1].SlotCount.Should().Be(1);
    }
}
