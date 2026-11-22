using AwesomeAssertions;
using Humans.Tasks.Data;
using Humans.Tasks.Domain;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NSubstitute;
using Xunit;
using static Humans.Tasks.Tests.TaskTestKit;

namespace Humans.Tasks.Tests;

public sealed class TaskGdprTests
{
    private readonly TaskTestKit _kit = new();
    private readonly Guid _person = Guid.NewGuid();

    [HumansFact]
    public async Task Export_ListsThePersonsAssignmentsAndRequestsOnly()
    {
        var mine = await _kit.CreateAsync(Input(title: "Mine"));
        var asked = await _kit.CreateAsync(Input(title: "Asked"));
        var other = await _kit.CreateAsync(Input(title: "Other"));
        await _kit.Service.AssignAsync(mine.Id, _person, _kit.Actor, Ct);
        _kit.Repository.Seed(new TaskAssignment { Id = Guid.NewGuid(), TaskItemId = asked.Id, UserId = _person, State = TaskAssignmentState.Requested });
        await _kit.Service.AssignAsync(other.Id, Guid.NewGuid(), _kit.Actor, Ct);

        var slices = await _kit.Service.ContributeForUserAsync(_person, Ct);

        var slice = slices.Should().ContainSingle().Which;
        slice.SectionName.Should().Be("Tasks");
        slice.Data.Should().BeAssignableTo<System.Collections.IEnumerable>()
            .Which.Cast<object>().Should().HaveCount(2);
        _kit.Service.ErasureDeclaration.Should().ContainKey("Tasks");
    }

    [HumansFact]
    public async Task Erasure_RemovesTheirPlacesAndLeavesTheFreeTextOnTheTask()
    {
        var task = await _kit.CreateAsync(Input(title: "Chainsaw work", description: "Needs a licence", needsApproval: true, reason: "chainsaw licence"));
        await _kit.Service.AssignAsync(task.Id, _person, _kit.Actor, Ct);
        await _kit.SetStatusAsync(task.Id, TaskItemStatus.Blocked, "waiting for the permit");

        await _kit.Service.EraseForUserAsync(_person, Ct);
        await _kit.Service.EraseForUserAsync(_person, Ct);

        var after = (await _kit.Service.GetTaskAsync(task.Id, Ct))!;
        after.Assignments.Should().BeEmpty();
        after.Description.Should().Be("Needs a licence");
        after.BlockedNote.Should().Be("waiting for the permit");
        after.ApprovalReason.Should().Be("chainsaw licence");
    }

    [HumansFact]
    public async Task Merge_MovesPlacesToTheSurvivor_AndConfirmedWinsWhereBothAreOnATask()
    {
        var (repository, factory) = Repository();
        var from = Guid.NewGuid();
        var to = Guid.NewGuid();
        var onlyFrom = Guid.NewGuid();
        var bothFromConfirmed = Guid.NewGuid();
        var bothToConfirmed = Guid.NewGuid();
        var bothRequested = Guid.NewGuid();
        await using (var db = await factory.CreateDbContextAsync(Ct))
        {
            foreach (var id in new[] { onlyFrom, bothFromConfirmed, bothToConfirmed, bothRequested })
                db.TaskItems.Add(new TaskItem { Id = id, OwnerKind = "Team", OwnerId = Production, Title = id.ToString() });
            db.TaskAssignments.AddRange(
                Row(onlyFrom, from, TaskAssignmentState.Requested),
                Row(bothFromConfirmed, from, TaskAssignmentState.Confirmed),
                Row(bothFromConfirmed, to, TaskAssignmentState.Requested),
                Row(bothToConfirmed, from, TaskAssignmentState.Requested),
                Row(bothToConfirmed, to, TaskAssignmentState.Confirmed),
                Row(bothRequested, from, TaskAssignmentState.Requested),
                Row(bothRequested, to, TaskAssignmentState.Requested));
            await db.SaveChangesAsync(Ct);
        }

        var moved = await repository.ReassignUserAsync(from, to, Ct);
        var again = await repository.ReassignUserAsync(from, to, Ct);

        moved.Should().Be(4);
        again.Should().Be(0);
        await using var check = await factory.CreateDbContextAsync(Ct);
        var rows = await check.TaskAssignments.ToListAsync(Ct);
        rows.Should().OnlyContain(r => r.UserId == to);
        rows.Should().HaveCount(4, because: "one row is kept per task");
        State(rows, onlyFrom).Should().Be(TaskAssignmentState.Requested);
        State(rows, bothFromConfirmed).Should().Be(TaskAssignmentState.Confirmed);
        State(rows, bothToConfirmed).Should().Be(TaskAssignmentState.Confirmed);
        State(rows, bothRequested).Should().Be(TaskAssignmentState.Requested);
    }

    [HumansFact]
    public async Task Merge_WritesAnAuditEntryForTheSurvivor()
    {
        var kit = new TaskTestKit();
        var repository = Substitute.For<ITaskRepository>();
        repository.ReassignUserAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(2);
        var service = new Humans.Tasks.Services.TaskService(
            repository, kit.Teams, kit.Settings, kit.Audit, new NodaTime.Testing.FakeClock(Instant.FromUtc(2027, 1, 1, 0, 0)), Substitute.For<Microsoft.Extensions.Localization.IStringLocalizer<TasksResource>>());
        var to = Guid.NewGuid();

        await service.ReassignAsync(Guid.NewGuid(), to, kit.Actor, Instant.FromUtc(2027, 1, 1, 0, 0), Ct);

        await kit.Audit.Received(1).LogAsync(
            TasksAuditActions.TaskAssigned, Arg.Any<string>(), to, Arg.Any<string>(), kit.Actor, Arg.Any<Guid?>(), Arg.Any<string?>());
    }

    private static TaskAssignmentState State(List<TaskAssignment> rows, Guid taskId) =>
        rows.Single(r => r.TaskItemId == taskId).State;

    private static TaskAssignment Row(Guid taskId, Guid userId, TaskAssignmentState state) =>
        new() { Id = Guid.NewGuid(), TaskItemId = taskId, UserId = userId, State = state };

    private static (TaskRepository Repository, IDbContextFactory<TasksDbContext> Factory) Repository()
    {
        var options = new DbContextOptionsBuilder<TasksDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var factory = new Factory(options);
        return (new TaskRepository(factory), factory);
    }

    private sealed class Factory(DbContextOptions<TasksDbContext> options) : IDbContextFactory<TasksDbContext>
    {
        public TasksDbContext CreateDbContext() => new(options);
    }
}
