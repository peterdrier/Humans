using AwesomeAssertions;
using Humans.Application.Constants;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Models;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
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

    [HumansFact]
    public async Task GetShiftDetails_with_block_id_returns_range_summary_for_caller()
    {
        var userId = Guid.NewGuid();
        var blockId = Guid.NewGuid();
        var (event_, rota) = MakeEventAndRota("Cantina build");

        var signups = new[]
        {
            MakeSignup(userId, blockId, MakeShift(rota, dayOffset: -3, allDay: true)),
            MakeSignup(userId, blockId, MakeShift(rota, dayOffset: -2, allDay: true)),
            MakeSignup(userId, blockId, MakeShift(rota, dayOffset: -1, allDay: true))
        };

        var dispatcher = MakeDispatcher(
            shiftMgr: new FakeShiftManagementService(event_),
            signupSvc: new FakeShiftSignupService(userId, signups));

        var result = await dispatcher.DispatchAsync(
            new AnthropicToolCall("t1", AgentToolNames.GetShiftDetails,
                $$"""{"shiftId":"{{blockId}}"}"""),
            userId,
            conversationId: Guid.NewGuid(),
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("Cantina build");
        result.Content.Should().Contain("3 days");
        result.Content.Should().Contain("Status: Confirmed");
        result.Content.Should().Contain("all-day");
    }

    [HumansFact]
    public async Task GetShiftDetails_with_singleton_signup_id_returns_single_date()
    {
        var userId = Guid.NewGuid();
        var (event_, rota) = MakeEventAndRota("Setup crew");
        var shift = MakeShift(rota, dayOffset: 0, allDay: false);
        shift.StartTime = new LocalTime(10, 0);
        shift.Duration = Duration.FromHours(4);

        var signup = MakeSignup(userId, signupBlockId: null, shift);
        signup.Status = SignupStatus.Pending;

        var dispatcher = MakeDispatcher(
            shiftMgr: new FakeShiftManagementService(event_),
            signupSvc: new FakeShiftSignupService(userId, new[] { signup }));

        var result = await dispatcher.DispatchAsync(
            new AnthropicToolCall("t1", AgentToolNames.GetShiftDetails,
                $$"""{"shiftId":"{{signup.Id}}"}"""),
            userId,
            conversationId: Guid.NewGuid(),
            CancellationToken.None);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("Setup crew");
        result.Content.Should().Contain("Status: Pending");
        result.Content.Should().Contain("10:00");
    }

    [HumansFact]
    public async Task GetShiftDetails_with_other_users_id_returns_not_found_error()
    {
        var callerId = Guid.NewGuid();
        var (event_, _) = MakeEventAndRota("Gate");
        var dispatcher = MakeDispatcher(
            shiftMgr: new FakeShiftManagementService(event_),
            signupSvc: new FakeShiftSignupService(callerId, Array.Empty<ShiftSignup>()));

        var someoneElsesShiftId = Guid.NewGuid();

        var result = await dispatcher.DispatchAsync(
            new AnthropicToolCall("t1", AgentToolNames.GetShiftDetails,
                $$"""{"shiftId":"{{someoneElsesShiftId}}"}"""),
            callerId,
            conversationId: Guid.NewGuid(),
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Be("Shift not found.");
    }

    [HumansFact]
    public async Task GetShiftDetails_with_invalid_uuid_returns_error()
    {
        var dispatcher = MakeDispatcher();

        var result = await dispatcher.DispatchAsync(
            new AnthropicToolCall("t1", AgentToolNames.GetShiftDetails,
                """{"shiftId":"not-a-uuid"}"""),
            Guid.NewGuid(),
            conversationId: Guid.NewGuid(),
            CancellationToken.None);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("Invalid shiftId");
    }

    private static Humans.Infrastructure.Services.Agent.AgentToolDispatcher MakeDispatcher(
        IShiftManagementService? shiftMgr = null,
        IShiftSignupService? signupSvc = null,
        IClock? clock = null)
    {
        var env = new TestHostEnvironment();
        var sections = new Humans.Infrastructure.Services.Preload.AgentSectionDocReader(env);
        var features = new Humans.Infrastructure.Services.Preload.AgentFeatureSpecReader(env);
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<Humans.Infrastructure.Services.Agent.AgentToolDispatcher>.Instance;
        return new Humans.Infrastructure.Services.Agent.AgentToolDispatcher(
            sections,
            features,
            signupSvc ?? new FakeShiftSignupService(Guid.Empty, Array.Empty<ShiftSignup>()),
            shiftMgr ?? new FakeShiftManagementService(eventSettings: null),
            clock ?? SystemClock.Instance,
            logger);
    }

    private static (EventSettings event_, Rota rota) MakeEventAndRota(string rotaName)
    {
        var event_ = new EventSettings
        {
            Id = Guid.NewGuid(),
            GateOpeningDate = new LocalDate(2026, 7, 4),
            TimeZoneId = "UTC",
            EventEndOffset = 7,
        };
        var rota = new Rota
        {
            Id = Guid.NewGuid(),
            Name = rotaName,
            EventSettingsId = event_.Id,
            EventSettings = event_,
        };
        return (event_, rota);
    }

    private static Shift MakeShift(Rota rota, int dayOffset, bool allDay) =>
        new()
        {
            Id = Guid.NewGuid(),
            RotaId = rota.Id,
            DayOffset = dayOffset,
            IsAllDay = allDay,
            Rota = rota,
            StartTime = new LocalTime(8, 0),
            Duration = Duration.FromHours(10),
            MaxVolunteers = 10,
        };

    private static ShiftSignup MakeSignup(Guid userId, Guid? signupBlockId, Shift shift) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            ShiftId = shift.Id,
            Shift = shift,
            SignupBlockId = signupBlockId,
            Status = SignupStatus.Confirmed,
        };

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

    private sealed class FakeShiftSignupService : IShiftSignupService
    {
        private readonly Guid _userId;
        private readonly IReadOnlyList<ShiftSignup> _signups;

        public FakeShiftSignupService(Guid userId, IReadOnlyList<ShiftSignup> signups)
        {
            _userId = userId;
            _signups = signups;
        }

        public Task<IReadOnlyList<ShiftSignup>> GetByUserAsync(Guid userId, Guid? eventSettingsId = null)
        {
            if (userId == _userId) return Task.FromResult(_signups);
            return Task.FromResult<IReadOnlyList<ShiftSignup>>(Array.Empty<ShiftSignup>());
        }

        public Task<SignupResult> SignUpAsync(Guid userId, Guid shiftId, Guid? actorUserId = null, bool isPrivileged = false) => throw new NotSupportedException();
        public Task<SignupResult> ApproveAsync(Guid signupId, Guid reviewerUserId) => throw new NotSupportedException();
        public Task<SignupResult> RefuseAsync(Guid signupId, Guid reviewerUserId, string? reason) => throw new NotSupportedException();
        public Task<SignupResult> BailAsync(Guid signupId, Guid actorUserId, string? reason) => throw new NotSupportedException();
        public Task<SignupResult> VoluntellAsync(Guid userId, Guid shiftId, Guid enrollerUserId) => throw new NotSupportedException();
        public Task<SignupResult> VoluntellRangeAsync(Guid userId, Guid rotaId, int startDayOffset, int endDayOffset, Guid enrollerUserId) => throw new NotSupportedException();
        public Task<SignupResult> MarkNoShowAsync(Guid signupId, Guid reviewerUserId) => throw new NotSupportedException();
        public Task<SignupResult> RemoveSignupAsync(Guid signupId, Guid removedByUserId, string? reason) => throw new NotSupportedException();
        public Task<SignupResult> SignUpRangeAsync(Guid userId, Guid rotaId, int startDayOffset, int endDayOffset, Guid? actorUserId = null, bool isPrivileged = false, bool skipConflicts = false) => throw new NotSupportedException();
        public Task<IReadOnlyList<ShiftSignup>> GetActiveSignupsForUserAsync(Guid userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SignupResult> ApproveRangeAsync(Guid signupBlockId, Guid reviewerUserId) => throw new NotSupportedException();
        public Task<SignupResult> RefuseRangeAsync(Guid signupBlockId, Guid reviewerUserId, string? reason) => throw new NotSupportedException();
        public Task BailRangeAsync(Guid signupBlockId, Guid actorUserId, string? reason = null) => throw new NotSupportedException();
        public Task<ShiftSignup?> GetByIdAsync(Guid signupId) => throw new NotSupportedException();
        public Task<ShiftSignup?> GetByBlockIdFirstAsync(Guid signupBlockId) => throw new NotSupportedException();
        public Task<IReadOnlyList<ShiftSignup>> GetByShiftAsync(Guid shiftId) => throw new NotSupportedException();
        public Task<IReadOnlyList<ShiftSignup>> GetNoShowHistoryAsync(Guid userId) => throw new NotSupportedException();
        public Task<(HashSet<Guid> ShiftIds, Dictionary<Guid, SignupStatus> Statuses)> GetActiveSignupStatusesAsync(Guid userId, Guid eventSettingsId) => throw new NotSupportedException();
        public Task<IReadOnlyList<(Guid SignupId, Guid ShiftId)>> CancelActiveSignupsForUserAsync(Guid userId, string reason, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ShiftSignup>> GetAllForOrphanScanAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task PromoteWidgetPendingSignupsAfterAdmissionAsync(Guid userId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ShiftSignup>> FilterToIncompleteOnboardingAsync(IReadOnlyList<ShiftSignup> signups, CancellationToken ct = default) => throw new NotSupportedException();
    }

    private sealed class FakeShiftManagementService : IShiftManagementService
    {
        private readonly EventSettings? _active;
        public FakeShiftManagementService(EventSettings? eventSettings) => _active = eventSettings;
        public Task<EventSettings?> GetActiveAsync() => Task.FromResult(_active);

        public Task<bool> IsDeptCoordinatorAsync(Guid userId, Guid departmentTeamId) => throw new NotSupportedException();
        public Task<bool> CanApproveSignupsAsync(Guid userId, Guid departmentTeamId) => throw new NotSupportedException();
        public Task<IReadOnlyList<Guid>> GetCoordinatorTeamIdsAsync(Guid userId) => throw new NotSupportedException();
        public Task<EventSettings?> GetByIdAsync(Guid id) => throw new NotSupportedException();
        public Task CreateAsync(EventSettings entity) => throw new NotSupportedException();
        public Task UpdateAsync(EventSettings entity) => throw new NotSupportedException();
        public int GetAvailableEeSlots(EventSettings settings, int dayOffset) => throw new NotSupportedException();
        public Task CreateRotaAsync(Rota rota) => throw new NotSupportedException();
        public Task UpdateRotaAsync(Rota rota) => throw new NotSupportedException();
        public Task MoveRotaToTeamAsync(Guid rotaId, Guid targetTeamId, Guid actorUserId) => throw new NotSupportedException();
        public Task DeleteRotaAsync(Guid rotaId) => throw new NotSupportedException();
        public Task<Rota?> GetRotaByIdAsync(Guid rotaId) => throw new NotSupportedException();
        public Task<IReadOnlyList<Rota>> GetRotasByDepartmentAsync(Guid teamId, Guid eventSettingsId) => throw new NotSupportedException();
        public Task CreateBuildStrikeShiftsAsync(Guid rotaId, Dictionary<int, (int Min, int Max)> dailyStaffing) => throw new NotSupportedException();
        public Task GenerateEventShiftsAsync(Guid rotaId, int startDayOffset, int endDayOffset, List<(LocalTime StartTime, double DurationHours)> timeSlots, int minVolunteers = 2, int maxVolunteers = 5) => throw new NotSupportedException();
        public Task CreateShiftAsync(Shift shift) => throw new NotSupportedException();
        public Task UpdateShiftAsync(Shift shift) => throw new NotSupportedException();
        public Task DeleteShiftAsync(Guid shiftId) => throw new NotSupportedException();
        public Task<Shift?> GetShiftByIdAsync(Guid shiftId) => throw new NotSupportedException();
        public Task<IReadOnlyList<Shift>> GetShiftsByRotaAsync(Guid rotaId) => throw new NotSupportedException();
        public (Instant Start, Instant End, ShiftPeriod Period) ResolveShiftTimes(Shift shift, EventSettings eventSettings) => throw new NotSupportedException();
        public Task<IReadOnlyList<Humans.Application.Interfaces.Shifts.UrgentShift>> GetUrgentShiftsAsync(Guid eventSettingsId, int? limit = null, Guid? departmentId = null, LocalDate? startDate = null, LocalDate? endDate = null, ShiftPeriod? period = null, BuildSubPeriod? subPeriod = null) => throw new NotSupportedException();
        public Task<IReadOnlyList<Humans.Application.Interfaces.Shifts.UrgentShift>> GetBrowseShiftsAsync(Guid eventSettingsId, Guid? departmentId = null, LocalDate? fromDate = null, LocalDate? toDate = null, bool includeAdminOnly = false, bool includeSignups = false, bool includeHidden = false, bool priorityOnly = false) => throw new NotSupportedException();
        public double CalculateScore(Shift shift, int confirmedCount, EventSettings eventSettings) => throw new NotSupportedException();
        public Task<IReadOnlyList<Humans.Application.Interfaces.Shifts.DailyStaffingData>> GetStaffingDataAsync(Guid eventSettingsId, Guid? departmentId = null, ShiftPeriod? period = null, BuildSubPeriod? subPeriod = null) => throw new NotSupportedException();
        public Task<IReadOnlyList<Humans.Application.Interfaces.Shifts.DailyStaffingHours>> GetStaffingHoursAsync(Guid eventSettingsId, Guid? departmentId = null, ShiftPeriod? period = null, BuildSubPeriod? subPeriod = null) => throw new NotSupportedException();
        public Task<Humans.Application.Interfaces.Shifts.ShiftsSummaryData?> GetShiftsSummaryAsync(Guid eventSettingsId, Guid departmentTeamId) => throw new NotSupportedException();
        public Task<Humans.Application.Interfaces.Shifts.ShiftsSummaryData?> GetShiftsSummaryForTeamsAsync(Guid eventSettingsId, IReadOnlyList<Guid> teamIds) => throw new NotSupportedException();
        public Task<IReadOnlyList<(Guid TeamId, string TeamName)>> GetDepartmentsWithRotasAsync(Guid eventSettingsId) => throw new NotSupportedException();
        public Task<IReadOnlyList<Guid>> GetTeamIdsWithShiftsInEventAsync(Guid eventSettingsId, IReadOnlyCollection<Guid> teamIds, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<Humans.Application.DTOs.DashboardOverview> GetDashboardOverviewAsync(Guid eventSettingsId, ShiftPeriod? period = null, BuildSubPeriod? subPeriod = null) => throw new NotSupportedException();
        public Task<IReadOnlyList<Humans.Application.DTOs.CoordinatorActivityRow>> GetCoordinatorActivityAsync(Guid eventSettingsId, ShiftPeriod? period = null, BuildSubPeriod? subPeriod = null) => throw new NotSupportedException();
        public Task<IReadOnlyList<Humans.Application.DTOs.DashboardTrendPoint>> GetDashboardTrendsAsync(Guid eventSettingsId, Humans.Application.Enums.TrendWindow window, ShiftPeriod? period = null, BuildSubPeriod? subPeriod = null) => throw new NotSupportedException();
        public Task<IReadOnlyList<Humans.Application.DTOs.DailyDepartmentStaffing>> GetDailyDepartmentStaffingAsync(Guid eventSettingsId, ShiftPeriod? period, BuildSubPeriod? subPeriod = null) => throw new NotSupportedException();
        public Task<IReadOnlyList<Humans.Application.DTOs.ShiftDurationBreakdownRow>> GetShiftDurationBreakdownAsync(Guid eventSettingsId, ShiftPeriod? period, BuildSubPeriod? subPeriod = null) => throw new NotSupportedException();
        public Task<Humans.Application.DTOs.CoverageHeatmap> GetCoverageHeatmapAsync(Guid eventSettingsId, ShiftPeriod? period, BuildSubPeriod? subPeriod = null) => throw new NotSupportedException();
        public Task<(int Filled, int Total, double Ratio)> GetOverallCoverageAsync(CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ShiftTag>> GetAllTagsAsync() => throw new NotSupportedException();
        public Task<IReadOnlyList<ShiftTag>> SearchTagsAsync(string query) => throw new NotSupportedException();
        public Task<ShiftTag> GetOrCreateTagAsync(string name) => throw new NotSupportedException();
        public Task SetRotaTagsAsync(Guid rotaId, IReadOnlyList<Guid> tagIds) => throw new NotSupportedException();
        public Task<IReadOnlyList<ShiftTag>> GetVolunteerTagPreferencesAsync(Guid userId) => throw new NotSupportedException();
        public Task SetVolunteerTagPreferencesAsync(Guid userId, IReadOnlyList<Guid> tagIds) => throw new NotSupportedException();
        public Task<IReadOnlyDictionary<Guid, int>> GetPendingShiftSignupCountsByTeamAsync(Guid eventSettingsId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<VolunteerEventProfile> GetOrCreateShiftProfileAsync(Guid userId) => throw new NotSupportedException();
        public Task UpdateShiftProfileAsync(VolunteerEventProfile profile) => throw new NotSupportedException();
        public Task<VolunteerEventProfile?> GetShiftProfileAsync(Guid userId, bool includeMedical) => throw new NotSupportedException();
        public Task<int> DeleteShiftProfilesForUserAsync(Guid userId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
