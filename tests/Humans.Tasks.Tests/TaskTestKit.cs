using AwesomeAssertions;
using Humans.Audit.Contracts;
using Humans.Base.Enums;
using Humans.Settings.Contracts;
using Humans.Tasks.Data;
using Humans.Tasks.Domain;
using Humans.Tasks.Services;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Tasks.Tests;

/// <summary>A service over an in-memory repository, two teams and two events, with the audit log captured.</summary>
internal sealed class TaskTestKit
{
    public static readonly Guid Production = Guid.NewGuid();
    public static readonly Guid Art = Guid.NewGuid();
    public static readonly Guid HiddenTeam = Guid.NewGuid();
    public static readonly Guid Everyone = Guid.NewGuid();
    public static readonly Guid Retired = Guid.NewGuid();

    public static readonly Guid Spring = Guid.NewGuid();
    public static readonly Guid Autumn = Guid.NewGuid();
    public static readonly Guid Withdrawn = Guid.NewGuid();

    public static readonly LocalDate SpringGate = new(2027, 3, 10);
    public static readonly LocalDate AutumnGate = new(2027, 9, 20);

    public Guid Actor { get; } = Guid.NewGuid();

    public FakeTaskRepository Repository { get; } = new();

    public IAuditLogService Audit { get; } = Substitute.For<IAuditLogService>();

    public ITeamService Teams { get; } = Substitute.For<ITeamService>();

    public ISettingsService Settings { get; } = Substitute.For<ISettingsService>();

    /// <summary>Knows every person, unless a test says otherwise.</summary>
    public IUserService Users { get; } = Substitute.For<IUserService>();

    public TaskService Service { get; }

    public TaskTestKit()
    {
        var teams = new[]
        {
            Team(Production, "Production", "production"),
            Team(Art, "Art", "art"),
            Team(HiddenTeam, "Secret", "secret", hidden: true),
            Team(Everyone, "Volunteers", "volunteers", system: true),
            Team(Retired, "Retired", "retired", active: false),
        };
        Teams.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(teams.ToDictionary(t => t.Id));
        Teams.GetTeamAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => teams.FirstOrDefault(t => t.Id == call.Arg<Guid>()));
        Teams.GetTeamBySlugAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => teams.FirstOrDefault(t => string.Equals(t.Slug, call.Arg<string>(), StringComparison.OrdinalIgnoreCase)));

        var events = new[]
        {
            Event(Spring, "Spring", SpringGate, EventSettingsStatus.Active),
            Event(Autumn, "Autumn", AutumnGate, EventSettingsStatus.Inactive),
            Event(Withdrawn, "Withdrawn", AutumnGate, EventSettingsStatus.Deleted),
        };
        Settings.GetAllEventSettingsAsync(Arg.Any<CancellationToken>()).Returns(events);
        Settings.GetActiveEventSettings().Returns(events[0]);

        Users.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => UserFixtures.MakeUserInfo(call.Arg<Guid>()));

        Service = new TaskService(
            Repository, Teams, Settings, Users, Audit,
            new FakeClock(Instant.FromUtc(2027, 1, 5, 12, 0)),
            new KeyLocalizer(),
            NullLogger<TaskService>.Instance);
    }

    public static TaskInput Input(
        Guid? team = null,
        string title = "Build the bar",
        Guid? eventId = null,
        TaskDateInput? start = null,
        TaskDateInput? due = null,
        int slots = 1,
        bool needsApproval = false,
        string? reason = null,
        bool hidden = false,
        string? description = null) => new(
        team ?? Production, title, description, eventId, start, due, SlotPriority.Important, slots, needsApproval, reason, hidden);

    public async Task<TaskInfo> CreateAsync(TaskInput? input = null)
    {
        var result = await Service.CreateAsync(input ?? Input(), Actor, TestContext.Current.CancellationToken);
        return result.Task ?? throw new InvalidOperationException(result.Refusal);
    }

    public async Task SetStatusAsync(Guid taskId, TaskItemStatus status, string? note = null) =>
        (await Service.SetStatusAsync(taskId, status, note, Actor, TestContext.Current.CancellationToken))
            .Succeeded.Should().BeTrue();

    public static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TeamInfo Team(Guid id, string name, string slug, bool hidden = false, bool system = false, bool active = true) => new(
        id, name, null, slug, active, system, SystemTeamType.None, RequiresApproval: false,
        IsPublicPage: false, hidden, IsPromotedToDirectory: false, Instant.FromUtc(2026, 1, 1, 0, 0), []);

    private static EventSettingsInfo Event(Guid id, string name, LocalDate gate, EventSettingsStatus status) => new(
        id, name, gate.Year, "Europe/Madrid", gate, -14, 7, 10,
        new Dictionary<int, int>(), null, null, status);

    internal sealed class KeyLocalizer : IStringLocalizer<TasksResource>
    {
        public LocalizedString this[string name] => new(name, name);

        public LocalizedString this[string name, params object[] arguments] => new(name, name);

        public IEnumerable<LocalizedString> GetAllStrings(bool includeParentCultures) => [];
    }
}

/// <summary>Keeps rows apart from what the service holds, as a database does: every read is a fresh copy.</summary>
internal sealed class FakeTaskRepository : ITaskRepository
{
    private readonly Dictionary<Guid, TaskItem> _tasks = [];
    private readonly List<TaskAssignment> _assignments = [];
    private readonly List<TaskDependency> _dependencies = [];

    public Task<IReadOnlyList<TaskItem>> GetAllAsync(CancellationToken ct = default)
    {
        IReadOnlyList<TaskItem> all = [.. _tasks.Values.Select(Copy)];
        return Task.FromResult(all);
    }

    public Task AddAsync(TaskItem task, CancellationToken ct = default)
    {
        _tasks[task.Id] = Copy(task);
        return Task.CompletedTask;
    }

    public Task AddManyAsync(IReadOnlyList<TaskItem> tasks, IReadOnlyList<TaskDependency> dependencies, CancellationToken ct = default)
    {
        foreach (var task in tasks)
            _tasks[task.Id] = Copy(task);
        _dependencies.AddRange(dependencies);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(TaskItem task, CancellationToken ct = default)
    {
        _tasks[task.Id] = Copy(task);
        return Task.CompletedTask;
    }

    public Task<bool> DeleteAsync(Guid taskId, CancellationToken ct = default)
    {
        _assignments.RemoveAll(a => a.TaskItemId == taskId);
        _dependencies.RemoveAll(d => d.TaskItemId == taskId || d.DependsOnTaskItemId == taskId);
        return Task.FromResult(_tasks.Remove(taskId));
    }

    public Task<bool> AddAssignmentAsync(TaskAssignment assignment, CancellationToken ct = default)
    {
        if (_assignments.Any(a => a.TaskItemId == assignment.TaskItemId && a.UserId == assignment.UserId))
            return Task.FromResult(false);
        _assignments.Add(assignment);
        return Task.FromResult(true);
    }

    public Task<bool> RemoveAssignmentAsync(Guid taskId, Guid userId, CancellationToken ct = default) =>
        Task.FromResult(_assignments.RemoveAll(a => a.TaskItemId == taskId && a.UserId == userId) > 0);

    public Task<bool> AddDependencyAsync(TaskDependency dependency, CancellationToken ct = default)
    {
        if (_dependencies.Any(d => d.TaskItemId == dependency.TaskItemId && d.DependsOnTaskItemId == dependency.DependsOnTaskItemId))
            return Task.FromResult(false);
        _dependencies.Add(dependency);
        return Task.FromResult(true);
    }

    public Task<bool> RemoveDependencyAsync(Guid taskId, Guid dependsOnTaskId, CancellationToken ct = default) =>
        Task.FromResult(_dependencies.RemoveAll(d => d.TaskItemId == taskId && d.DependsOnTaskItemId == dependsOnTaskId) > 0);

    public Task RemoveUserAssignmentsAsync(Guid userId, CancellationToken ct = default)
    {
        _assignments.RemoveAll(a => a.UserId == userId);
        return Task.CompletedTask;
    }

    public Task<int> ReassignUserAsync(Guid fromUserId, Guid toUserId, CancellationToken ct = default) =>
        throw new NotSupportedException("Covered against EF in TaskGdprTests.");

    public void Seed(TaskAssignment assignment) => _assignments.Add(assignment);

    private TaskItem Copy(TaskItem t)
    {
        var copy = new TaskItem
        {
            Id = t.Id,
            CreatedAt = t.CreatedAt,
            OwnerKind = t.OwnerKind,
            OwnerId = t.OwnerId,
            Title = t.Title,
            Description = t.Description,
            EventId = t.EventId,
            StartDate = t.StartDate,
            StartDayOffset = t.StartDayOffset,
            DueDate = t.DueDate,
            DueDayOffset = t.DueDayOffset,
            Status = t.Status,
            BlockedNote = t.BlockedNote,
            Priority = t.Priority,
            SlotCount = t.SlotCount,
            NeedsApproval = t.NeedsApproval,
            ApprovalReason = t.ApprovalReason,
            IsHidden = t.IsHidden,
            UpdatedAt = t.UpdatedAt,
        };
        foreach (var a in _assignments.Where(a => a.TaskItemId == t.Id))
            copy.Assignments.Add(new TaskAssignment { Id = a.Id, TaskItemId = a.TaskItemId, UserId = a.UserId, State = a.State, CreatedAt = a.CreatedAt });
        foreach (var d in _dependencies.Where(d => d.TaskItemId == t.Id))
            copy.Dependencies.Add(new TaskDependency { Id = d.Id, TaskItemId = d.TaskItemId, DependsOnTaskItemId = d.DependsOnTaskItemId });
        return copy;
    }
}
