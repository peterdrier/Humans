using AwesomeAssertions;
using Humans.Application.Constants;
using Humans.Application.Models;
using NSubstitute;
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
    public async Task GetAuditHistory_renders_lines_with_viewer_substitution_and_filters_unmapped()
    {
        var viewer = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var stub = new StubAuditViewer
        {
            Events = new[]
            {
                BuildVoluntoldEvent(actor, viewer),
                BuildUnmappedEvent()
            }
        };

        var dispatcher = MakeDispatcher(stub);

        var result = await dispatcher.DispatchAsync(
            new Humans.Application.Models.AnthropicToolCall("t1", Humans.Application.Constants.AgentToolNames.GetAuditHistory, """{"limit":5}"""),
            userId: viewer,
            conversationId: Guid.NewGuid(),
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("voluntold You");
        result.Content.Should().NotContain(viewer.ToString());
        result.Content.Should().NotContain(actor.ToString());
        // Unmapped event filtered out — only one line.
        result.Content.Split('\n').Should().HaveCount(1);
    }

    [HumansFact]
    public async Task GetAuditHistory_empty_history_returns_friendly_message()
    {
        var dispatcher = MakeDispatcher(new StubAuditViewer { Events = Array.Empty<Humans.Application.Services.AuditLog.AuditEvent>() });

        var result = await dispatcher.DispatchAsync(
            new Humans.Application.Models.AnthropicToolCall("t1", Humans.Application.Constants.AgentToolNames.GetAuditHistory, "{}"),
            userId: Guid.NewGuid(),
            conversationId: Guid.NewGuid(),
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Be("No audit history for this user.");
    }

    [HumansFact]
    public async Task GetAuditHistory_caps_limit_at_50_and_defaults_to_20()
    {
        // The dispatcher passes `limit` straight to GetForUserAsync after
        // clamping. We capture the value used.
        var stub = new StubAuditViewer { Events = Array.Empty<Humans.Application.Services.AuditLog.AuditEvent>() };
        var dispatcher = MakeDispatcher(stub);

        // Request 999 → clamps to 50.
        await dispatcher.DispatchAsync(
            new Humans.Application.Models.AnthropicToolCall("t1", Humans.Application.Constants.AgentToolNames.GetAuditHistory, """{"limit":999}"""),
            userId: Guid.NewGuid(), conversationId: Guid.NewGuid(), CancellationToken.None);
        stub.LastLimit.Should().Be(50);

        // Omit limit → defaults to 20.
        await dispatcher.DispatchAsync(
            new Humans.Application.Models.AnthropicToolCall("t1", Humans.Application.Constants.AgentToolNames.GetAuditHistory, "{}"),
            userId: Guid.NewGuid(), conversationId: Guid.NewGuid(), CancellationToken.None);
        stub.LastLimit.Should().Be(20);

        // Request 0 → clamps to 1 (minimum).
        await dispatcher.DispatchAsync(
            new Humans.Application.Models.AnthropicToolCall("t1", Humans.Application.Constants.AgentToolNames.GetAuditHistory, """{"limit":0}"""),
            userId: Guid.NewGuid(), conversationId: Guid.NewGuid(), CancellationToken.None);
        stub.LastLimit.Should().Be(1);
    }

    private static Humans.Application.Services.AuditLog.AuditEvent BuildVoluntoldEvent(Guid actor, Guid subject) =>
        new(
            Id: Guid.NewGuid(),
            OccurredAt: NodaTime.Instant.FromUtc(2026, 4, 30, 17, 0),
            Action: Humans.Domain.Enums.AuditAction.ShiftSignupVoluntold,
            ActorUserId: actor,
            ActorDisplayName: "Frank",
            EntityType: "ShiftSignup",
            EntityId: Guid.NewGuid(),
            SubjectUserId: subject,
            SubjectDisplayName: "Peter",
            TargetTeamId: null,
            TargetTeamName: null,
            TargetTeamSlug: null,
            RelatedEntityId: subject,
            RelatedEntityType: "User",
            Description: "shift 'Cantina'",
            Role: null,
            UserEmail: null,
            Success: null,
            ErrorMessage: null,
            SyncSource: null,
            ResourceId: null,
            ResourceName: null);

    private static Humans.Application.Services.AuditLog.AuditEvent BuildUnmappedEvent() =>
        new(
            Id: Guid.NewGuid(),
            OccurredAt: NodaTime.Instant.FromUtc(2026, 4, 30, 17, 0),
            Action: Humans.Domain.Enums.AuditAction.AnomalousPermissionDetected,
            ActorUserId: null,
            ActorDisplayName: null,
            EntityType: "GoogleResource",
            EntityId: Guid.NewGuid(),
            SubjectUserId: null,
            SubjectDisplayName: null,
            TargetTeamId: null,
            TargetTeamName: null,
            TargetTeamSlug: null,
            RelatedEntityId: null,
            RelatedEntityType: null,
            Description: "anomaly",
            Role: null,
            UserEmail: null,
            Success: null,
            ErrorMessage: null,
            SyncSource: null,
            ResourceId: null,
            ResourceName: null);

    [HumansFact]
    public async Task GetShiftDetails_with_block_id_returns_rota_name_dates_and_day_count()
    {
        var viewer = Guid.NewGuid();
        var blockId = Guid.NewGuid();
        var ev = MakeEventSettings();
        var rota = new Humans.Domain.Entities.Rota
        {
            Id = Guid.NewGuid(),
            Name = "Cantina build",
            PracticalInfo = "Meet at gate",
            Description = "Daily setup support"
        };
        var signups = Enumerable.Range(0, 7)
            .Select(i => MakeSignup(viewer, blockId, MakeShift(rota, dayOffset: -10 + i, isAllDay: true), Humans.Domain.Enums.SignupStatus.Confirmed))
            .ToList();

        var shiftSignups = Substitute.For<Humans.Application.Interfaces.Shifts.IShiftSignupService>();
        shiftSignups.GetByUserAsync(viewer, ev.Id).Returns((IReadOnlyList<Humans.Domain.Entities.ShiftSignup>)signups);

        var shiftMgmt = Substitute.For<Humans.Application.Interfaces.Shifts.IShiftManagementService>();
        shiftMgmt.GetActiveAsync().Returns(ev);

        var dispatcher = MakeDispatcher(shiftSignups: shiftSignups, shiftManagement: shiftMgmt);

        var result = await dispatcher.DispatchAsync(
            new AnthropicToolCall("t1", AgentToolNames.GetShiftDetails,
                $$"""{"shiftId":"{{blockId}}"}"""),
            userId: viewer,
            conversationId: Guid.NewGuid(),
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("Cantina build");
        result.Content.Should().Contain("Confirmed");
        result.Content.Should().Contain("7 days");
        result.Content.Should().Contain("Meet at gate");
        result.Content.Should().Contain("all-day shift");
    }

    [HumansFact]
    public async Task GetShiftDetails_with_singleton_id_returns_single_date()
    {
        var viewer = Guid.NewGuid();
        var ev = MakeEventSettings();
        var rota = new Humans.Domain.Entities.Rota { Id = Guid.NewGuid(), Name = "Setup crew" };
        var signup = MakeSignup(viewer, signupBlockId: null,
            MakeShift(rota, dayOffset: 0, isAllDay: false, startTime: new NodaTime.LocalTime(9, 0), durationHours: 4),
            Humans.Domain.Enums.SignupStatus.Pending);

        var shiftSignups = Substitute.For<Humans.Application.Interfaces.Shifts.IShiftSignupService>();
        shiftSignups.GetByUserAsync(viewer, ev.Id)
            .Returns((IReadOnlyList<Humans.Domain.Entities.ShiftSignup>)new[] { signup });

        var shiftMgmt = Substitute.For<Humans.Application.Interfaces.Shifts.IShiftManagementService>();
        shiftMgmt.GetActiveAsync().Returns(ev);

        var dispatcher = MakeDispatcher(shiftSignups: shiftSignups, shiftManagement: shiftMgmt);

        var result = await dispatcher.DispatchAsync(
            new AnthropicToolCall("t1", AgentToolNames.GetShiftDetails,
                $$"""{"shiftId":"{{signup.Id}}"}"""),
            userId: viewer,
            conversationId: Guid.NewGuid(),
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("Setup crew");
        result.Content.Should().Contain("Pending");
        result.Content.Should().NotContain("days)"); // no day-count blurb on singletons
    }

    [HumansFact]
    public async Task GetShiftDetails_with_unknown_id_returns_not_found_error()
    {
        var viewer = Guid.NewGuid();
        var ev = MakeEventSettings();

        var shiftSignups = Substitute.For<Humans.Application.Interfaces.Shifts.IShiftSignupService>();
        shiftSignups.GetByUserAsync(viewer, ev.Id)
            .Returns((IReadOnlyList<Humans.Domain.Entities.ShiftSignup>)Array.Empty<Humans.Domain.Entities.ShiftSignup>());

        var shiftMgmt = Substitute.For<Humans.Application.Interfaces.Shifts.IShiftManagementService>();
        shiftMgmt.GetActiveAsync().Returns(ev);

        var dispatcher = MakeDispatcher(shiftSignups: shiftSignups, shiftManagement: shiftMgmt);

        var result = await dispatcher.DispatchAsync(
            new AnthropicToolCall("t1", AgentToolNames.GetShiftDetails,
                $$"""{"shiftId":"{{Guid.NewGuid()}}"}"""),
            userId: viewer,
            conversationId: Guid.NewGuid(),
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("Shift not found");
    }

    [HumansFact]
    public async Task GetShiftDetails_does_not_leak_other_users_shifts()
    {
        // Lookup uses the viewer's own GetByUserAsync return — a foreign user's
        // signup id will not appear in that list, so we get a "not found"
        // result (no information leak about whose shift it is).
        var viewer = Guid.NewGuid();
        var foreignBlockId = Guid.NewGuid();
        var ev = MakeEventSettings();

        var shiftSignups = Substitute.For<Humans.Application.Interfaces.Shifts.IShiftSignupService>();
        // Viewer has zero signups.
        shiftSignups.GetByUserAsync(viewer, ev.Id)
            .Returns((IReadOnlyList<Humans.Domain.Entities.ShiftSignup>)Array.Empty<Humans.Domain.Entities.ShiftSignup>());

        var shiftMgmt = Substitute.For<Humans.Application.Interfaces.Shifts.IShiftManagementService>();
        shiftMgmt.GetActiveAsync().Returns(ev);

        var dispatcher = MakeDispatcher(shiftSignups: shiftSignups, shiftManagement: shiftMgmt);

        var result = await dispatcher.DispatchAsync(
            new AnthropicToolCall("t1", AgentToolNames.GetShiftDetails,
                $$"""{"shiftId":"{{foreignBlockId}}"}"""),
            userId: viewer,
            conversationId: Guid.NewGuid(),
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("Shift not found");
    }

    [HumansFact]
    public async Task GetShiftDetails_with_invalid_guid_returns_validation_error()
    {
        var dispatcher = MakeDispatcher();
        var result = await dispatcher.DispatchAsync(
            new AnthropicToolCall("t1", AgentToolNames.GetShiftDetails, """{"shiftId":"not-a-guid"}"""),
            userId: Guid.NewGuid(),
            conversationId: Guid.NewGuid(),
            CancellationToken.None);
        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("must be a valid GUID");
    }

    private static Humans.Domain.Entities.EventSettings MakeEventSettings() => new()
    {
        Id = Guid.NewGuid(),
        EventName = "Test",
        Year = 2026,
        TimeZoneId = "Europe/Madrid",
        GateOpeningDate = new NodaTime.LocalDate(2026, 7, 1),
        BuildStartOffset = -14,
        EventEndOffset = 6,
        StrikeEndOffset = 9,
        IsActive = true
    };

    private static Humans.Domain.Entities.Shift MakeShift(
        Humans.Domain.Entities.Rota rota, int dayOffset, bool isAllDay,
        NodaTime.LocalTime? startTime = null, double durationHours = 0) => new()
        {
            Id = Guid.NewGuid(),
            RotaId = rota.Id,
            Rota = rota,
            DayOffset = dayOffset,
            IsAllDay = isAllDay,
            StartTime = startTime ?? new NodaTime.LocalTime(8, 0),
            Duration = NodaTime.Duration.FromHours(durationHours),
            MinVolunteers = 1,
            MaxVolunteers = 5
        };

    private static Humans.Domain.Entities.ShiftSignup MakeSignup(
        Guid userId, Guid? signupBlockId,
        Humans.Domain.Entities.Shift shift,
        Humans.Domain.Enums.SignupStatus status) => new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ShiftId = shift.Id,
            Shift = shift,
            SignupBlockId = signupBlockId,
            Status = status
        };

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

    private static Humans.Infrastructure.Services.Agent.AgentToolDispatcher MakeDispatcher(
        Humans.Application.Interfaces.AuditLog.IAuditViewerService? auditViewer = null,
        Humans.Application.Interfaces.Shifts.IShiftSignupService? shiftSignups = null,
        Humans.Application.Interfaces.Shifts.IShiftManagementService? shiftManagement = null)
    {
        var env = new TestHostEnvironment();
        var sections = new Humans.Infrastructure.Services.Preload.AgentSectionDocReader(env);
        var features = new Humans.Infrastructure.Services.Preload.AgentFeatureSpecReader(env);
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<Humans.Infrastructure.Services.Agent.AgentToolDispatcher>.Instance;
        return new Humans.Infrastructure.Services.Agent.AgentToolDispatcher(
            sections,
            features,
            auditViewer ?? new StubAuditViewer(),
            shiftSignups ?? Substitute.For<Humans.Application.Interfaces.Shifts.IShiftSignupService>(),
            shiftManagement ?? Substitute.For<Humans.Application.Interfaces.Shifts.IShiftManagementService>(),
            logger);
    }

    private sealed class StubAuditViewer : Humans.Application.Interfaces.AuditLog.IAuditViewerService
    {
        public IReadOnlyList<Humans.Application.Services.AuditLog.AuditEvent> Events { get; set; } =
            Array.Empty<Humans.Application.Services.AuditLog.AuditEvent>();

        /// <summary>Captures the limit value passed to <see cref="GetForUserAsync"/> for clamp-behaviour assertions.</summary>
        public int? LastLimit { get; private set; }

        public Task<IReadOnlyList<Humans.Application.Services.AuditLog.AuditEvent>> GetRecentAsync(int count, CancellationToken ct = default) => Task.FromResult(Events);
        public Task<IReadOnlyList<Humans.Application.Services.AuditLog.AuditEvent>> GetForUserAsync(Guid userId, int count, CancellationToken ct = default)
        {
            LastLimit = count;
            return Task.FromResult(Events);
        }
        public Task<IReadOnlyList<Humans.Application.Services.AuditLog.AuditEvent>> GetForResourceAsync(Guid resourceId, CancellationToken ct = default) => Task.FromResult(Events);
        public Task<IReadOnlyList<Humans.Application.Services.AuditLog.AuditEvent>> GetGoogleSyncForUserAsync(Guid userId, CancellationToken ct = default) => Task.FromResult(Events);
        public Task<Humans.Application.Interfaces.AuditLog.AuditEventPage> GetPageAsync(string? actionFilter, int page, int pageSize, CancellationToken ct = default) =>
            Task.FromResult(new Humans.Application.Interfaces.AuditLog.AuditEventPage(Events, Events.Count, 0));
        public Task<IReadOnlyList<Humans.Application.Services.AuditLog.AuditEvent>> GetFilteredAsync(string? entityType, Guid? entityId, Guid? userId, IReadOnlyList<Humans.Domain.Enums.AuditAction>? actions, int limit, CancellationToken ct = default) => Task.FromResult(Events);
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
