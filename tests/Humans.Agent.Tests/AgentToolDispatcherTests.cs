using AwesomeAssertions;
using NSubstitute;
using Humans.Agent.Services;
using Humans.Agent.Services.Anthropic;
using Humans.Agent.Services.Preload;

using Humans.Shifts.Contracts;
namespace Humans.Agent.Tests;

public class AgentToolDispatcherTests
{
    [HumansFact]
    public async Task Unknown_tool_name_returns_error_result()
    {
        var dispatcher = MakeDispatcher();
        var result = await dispatcher.DispatchAsync(
            new AnthropicToolCall("t1", "delete_users", "{}"),
            userId: Guid.NewGuid(),
            Xunit.TestContext.Current.CancellationToken);

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
            Events =
            [
                BuildVoluntoldEvent(actor, viewer),
                BuildUnmappedEvent()
            ]
        };

        var dispatcher = MakeDispatcher(stub);

        var result = await dispatcher.DispatchAsync(
            new AnthropicToolCall("t1", AgentToolNames.GetAuditHistory, """{"limit":5}"""),
            userId: viewer,
            Xunit.TestContext.Current.CancellationToken);

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
        var dispatcher = MakeDispatcher(new StubAuditViewer { Events = [] });

        var result = await dispatcher.DispatchAsync(
            new AnthropicToolCall("t1", AgentToolNames.GetAuditHistory, "{}"),
            userId: Guid.NewGuid(),
            Xunit.TestContext.Current.CancellationToken);

        result.IsError.Should().BeFalse();
        result.Content.Should().Be("No audit history for this user.");
    }

    [HumansFact]
    public async Task GetAuditHistory_caps_limit_at_50_and_defaults_to_20()
    {
        // The dispatcher passes `limit` straight to GetForUserAsync after
        // clamping. We capture the value used.
        var stub = new StubAuditViewer { Events = [] };
        var dispatcher = MakeDispatcher(stub);

        // Request 999 → clamps to 50.
        await dispatcher.DispatchAsync(
            new AnthropicToolCall("t1", AgentToolNames.GetAuditHistory, """{"limit":999}"""),
            userId: Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);
        stub.LastLimit.Should().Be(50);

        // Omit limit → defaults to 20.
        await dispatcher.DispatchAsync(
            new AnthropicToolCall("t1", AgentToolNames.GetAuditHistory, "{}"),
            userId: Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);
        stub.LastLimit.Should().Be(20);

        // Request 0 → clamps to 1 (minimum).
        await dispatcher.DispatchAsync(
            new AnthropicToolCall("t1", AgentToolNames.GetAuditHistory, """{"limit":0}"""),
            userId: Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);
        stub.LastLimit.Should().Be(1);
    }

    private static Humans.AuditLog.Contracts.AuditEvent BuildVoluntoldEvent(Guid actor, Guid subject) =>
        new(
            Id: Guid.NewGuid(),
            OccurredAt: NodaTime.Instant.FromUtc(2026, 4, 30, 17, 0),
            Action: Humans.AuditLog.Contracts.AuditAction.ShiftSignupVoluntold,
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

    private static Humans.AuditLog.Contracts.AuditEvent BuildUnmappedEvent() =>
        new(
            Id: Guid.NewGuid(),
            OccurredAt: NodaTime.Instant.FromUtc(2026, 4, 30, 17, 0),
            Action: Humans.AuditLog.Contracts.AuditAction.AnomalousPermissionDetected,
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
        var rota = new RotaStub("Cantina build", "Meet at gate", "Daily setup support");
        var signups = Enumerable.Range(0, 7)
            .Select(i => MakeSignup(blockId, MakeShift(rota, dayOffset: -10 + i, isAllDay: true), SignupStatus.Confirmed))
            .ToList();

        var shiftView = MakeViewFor(viewer, signups);

        var burnSettings = Substitute.For<Humans.Shifts.Contracts.IBurnSettingsService>();
        burnSettings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(ev);

        var dispatcher = MakeDispatcher(shiftView: shiftView, burnSettings: burnSettings);

        var result = await dispatcher.DispatchAsync(
            new AnthropicToolCall("t1", AgentToolNames.GetShiftDetails,
                $$"""{"shiftId":"{{blockId}}"}"""),
            userId: viewer,
            Xunit.TestContext.Current.CancellationToken);

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
        var rota = new RotaStub("Setup crew");
        var signup = MakeSignup(signupBlockId: null,
            MakeShift(rota, dayOffset: 0, isAllDay: false, startTime: new NodaTime.LocalTime(9, 0), durationHours: 4),
            SignupStatus.Pending);

        var shiftView = MakeViewFor(viewer, [signup]);

        var burnSettings = Substitute.For<Humans.Shifts.Contracts.IBurnSettingsService>();
        burnSettings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(ev);

        var dispatcher = MakeDispatcher(shiftView: shiftView, burnSettings: burnSettings);

        var result = await dispatcher.DispatchAsync(
            new AnthropicToolCall("t1", AgentToolNames.GetShiftDetails,
                $$"""{"shiftId":"{{signup.Id}}"}"""),
            userId: viewer,
            Xunit.TestContext.Current.CancellationToken);

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

        var shiftView = MakeViewFor(viewer, []);

        var burnSettings = Substitute.For<Humans.Shifts.Contracts.IBurnSettingsService>();
        burnSettings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(ev);

        var dispatcher = MakeDispatcher(shiftView: shiftView, burnSettings: burnSettings);

        var result = await dispatcher.DispatchAsync(
            new AnthropicToolCall("t1", AgentToolNames.GetShiftDetails,
                $$"""{"shiftId":"{{Guid.NewGuid()}}"}"""),
            userId: viewer,
            Xunit.TestContext.Current.CancellationToken);

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

        // Viewer has zero signups in their cached view.
        var shiftView = MakeViewFor(viewer, []);

        var burnSettings = Substitute.For<Humans.Shifts.Contracts.IBurnSettingsService>();
        burnSettings.GetActiveAsync(Arg.Any<CancellationToken>()).Returns(ev);

        var dispatcher = MakeDispatcher(shiftView: shiftView, burnSettings: burnSettings);

        var result = await dispatcher.DispatchAsync(
            new AnthropicToolCall("t1", AgentToolNames.GetShiftDetails,
                $$"""{"shiftId":"{{foreignBlockId}}"}"""),
            userId: viewer,
            Xunit.TestContext.Current.CancellationToken);

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
            Xunit.TestContext.Current.CancellationToken);
        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("must be a valid GUID");
    }

    private static Humans.Shifts.Contracts.BurnSettingsInfo MakeEventSettings() => new(
        Id: Guid.NewGuid(),
        EventName: "Test",
        Year: 2026,
        TimeZoneId: "Europe/Madrid",
        GateOpeningDate: new NodaTime.LocalDate(2026, 7, 1),
        BuildStartOffset: -14,
        EventEndOffset: 6,
        StrikeEndOffset: 9,
        FirstCrewStartOffset: 0,
        SetupWeekStartOffset: 0,
        PreEventWeekStartOffset: 0,
        FinishingWeekendStartOffset: 0,
        EarlyEntryCapacity: new Dictionary<int, int>(),
        BarriosEarlyEntryAllocation: null,
        EarlyEntryClose: null,
        IsShiftBrowsingOpen: false);

    /// <summary>The rota fields get_shift_details renders, without the entity.</summary>
    private sealed record RotaStub(string Name, string? PracticalInfo = null, string? Description = null);

    /// <summary>
    /// One signup on the section's boundary shape, with the fields
    /// <c>RenderShiftDetails</c> reads resolved against
    /// <see cref="MakeEventSettings"/> exactly as the section's projection does.
    /// </summary>
    private static Humans.Shifts.Contracts.ShiftSignupSummary MakeShift(
        RotaStub rota, int dayOffset, bool isAllDay,
        NodaTime.LocalTime? startTime = null, double durationHours = 0)
    {
        var ev = MakeEventSettings();
        var date = ev.GateOpeningDate.PlusDays(dayOffset);
        var tz = NodaTime.DateTimeZoneProviders.Tzdb[ev.TimeZoneId];
        var start = isAllDay ? new NodaTime.LocalTime(8, 0) : startTime ?? new NodaTime.LocalTime(8, 0);
        var end = isAllDay ? new NodaTime.LocalTime(18, 0) : start.PlusHours((int)durationHours);
        return ShiftFixtures.Signup(
            rotaName: rota.Name,
            rotaPracticalInfo: rota.PracticalInfo,
            rotaDescription: rota.Description,
            eventSettingsId: ev.Id,
            date: date,
            isAllDay: isAllDay,
            windowStart: start,
            durationHours: isAllDay ? 10 : durationHours,
            absoluteStart: date.At(start).InZoneLeniently(tz).ToInstant(),
            absoluteEnd: date.At(end).InZoneLeniently(tz).ToInstant());
    }

    private static Humans.Shifts.Contracts.ShiftSignupSummary MakeSignup(
        Guid? signupBlockId,
        Humans.Shifts.Contracts.ShiftSignupSummary shift,
        SignupStatus status) =>
        shift with { Id = Guid.NewGuid(), SignupBlockId = signupBlockId, Status = status };

    [HumansFact]
    public async Task RouteToIssue_returns_proposal_marker_without_creating_anything()
    {
        var dispatcher = MakeDispatcher();

        var result = await dispatcher.DispatchAsync(
            new AnthropicToolCall("t1", AgentToolNames.RouteToIssue,
                """{"title":"Calendar feature","category":"Feature","description":"User asked about calendar; not implemented yet."}"""),
            userId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Xunit.TestContext.Current.CancellationToken);

        result.IsError.Should().BeFalse();
        result.Content.Should().Contain("Proposal queued");
    }

    [HumansFact]
    public async Task RouteToIssue_reports_an_error_when_max_tokens_truncated_its_arguments()
    {
        // nobodies-collective/Humans#963 dispatches max_tokens-truncated tool calls instead of
        // discarding them, which raised the question of whether route_to_issue — the one tool
        // whose result is a canned success string — could tell the model "Proposal queued" for
        // a payload AgentService then drops, leaving the user with no issue form. It can't:
        // DispatchAsync parses the arguments once, up front, for every tool, so a truncated
        // payload fails before the per-tool switch is ever reached. Pinning that ordering here
        // because the canned success sits below it and would be wrong if the parse ever moved.
        var dispatcher = MakeDispatcher();

        var result = await dispatcher.DispatchAsync(
            new AnthropicToolCall("t1", AgentToolNames.RouteToIssue,
                """{"title":"Calendar feature","category":"Feature","description":"User asked ab"""),
            userId: Guid.Parse("22222222-2222-2222-2222-222222222222"),
            Xunit.TestContext.Current.CancellationToken);

        result.IsError.Should().BeTrue(
            "the model must learn the proposal failed so it can retry, not be told it was queued");
        result.Content.Should().NotContain("Proposal queued");
    }

    [HumansFact]
    public async Task FetchCommunityFaq_returns_wrapped_body_for_known_topic()
    {
        var dispatcher = MakeDispatcher();

        var result = await dispatcher.DispatchAsync(
            new AnthropicToolCall("t1", AgentToolNames.FetchCommunityFaq, """{"topic":"FAQ-general"}"""),
            userId: Guid.NewGuid(),
            Xunit.TestContext.Current.CancellationToken);

        result.IsError.Should().BeFalse();
        result.Content.Should().StartWith("SOURCE: community Discord FAQ");
        result.Content.Should().Contain("NOT official");
    }

    [HumansFact]
    public async Task FetchCommunityFaq_returns_error_for_unknown_topic()
    {
        var dispatcher = MakeDispatcher();

        var result = await dispatcher.DispatchAsync(
            new AnthropicToolCall("t1", AgentToolNames.FetchCommunityFaq, """{"topic":"nope"}"""),
            userId: Guid.NewGuid(),
            Xunit.TestContext.Current.CancellationToken);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("Unknown community FAQ topic");
    }

    /// <summary>
    /// A miss must name the accepted keys. A bare "Unknown section: X" left the model nothing to
    /// correct with: 20 out-of-whitelist lookups across 9 production conversations, three of which
    /// ended in an empty reply to the user (nobodies-collective/Humans#949).
    /// </summary>
    [HumansFact]
    public async Task FetchSectionGuide_miss_lists_the_valid_section_keys()
    {
        var dispatcher = MakeDispatcher();

        var result = await dispatcher.DispatchAsync(
            new AnthropicToolCall("t1", AgentToolNames.FetchSectionGuide, """{"section":"NotASection"}"""),
            userId: Guid.NewGuid(),
            Xunit.TestContext.Current.CancellationToken);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("Unknown section: NotASection");
        result.Content.Should().Contain("Shifts").And.Contain("Users").And.Contain("Retry with one of these");
    }

    [HumansFact]
    public async Task FetchCommunityFaq_miss_lists_the_valid_topics()
    {
        var dispatcher = MakeDispatcher();

        var result = await dispatcher.DispatchAsync(
            new AnthropicToolCall("t1", AgentToolNames.FetchCommunityFaq, """{"topic":"nope"}"""),
            userId: Guid.NewGuid(),
            Xunit.TestContext.Current.CancellationToken);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("Valid keys are: FAQ-general");
    }

    [HumansFact]
    public async Task FetchFeatureSpec_miss_lists_the_valid_stems()
    {
        var dispatcher = MakeDispatcher();

        var result = await dispatcher.DispatchAsync(
            new AnthropicToolCall("t1", AgentToolNames.FetchFeatureSpec, """{"name":"no-such-spec"}"""),
            userId: Guid.NewGuid(),
            Xunit.TestContext.Current.CancellationToken);

        result.IsError.Should().BeTrue();
        result.Content.Should().Contain("Feature spec not found: no-such-spec");
        result.Content.Should().Contain("Valid keys are: 26-events, gate-admissions");
    }

    /// <summary>
    /// The key set is listed from GitHub; when that listing fails the miss must stay a plain
    /// message rather than telling the model an empty set is what it may ask for.
    /// </summary>
    [HumansFact]
    public async Task FetchFeatureSpec_miss_omits_the_key_list_when_the_folder_cannot_be_listed()
    {
        var dispatcher = MakeDispatcher(source: new UnlistableGuideSource());

        var result = await dispatcher.DispatchAsync(
            new AnthropicToolCall("t1", AgentToolNames.FetchFeatureSpec, """{"name":"no-such-spec"}"""),
            userId: Guid.NewGuid(),
            Xunit.TestContext.Current.CancellationToken);

        result.IsError.Should().BeTrue();
        result.Content.Should().Be("Feature spec not found: no-such-spec.");
    }

    /// <summary>A source whose folder listing is broken (revoked token, GitHub outage).</summary>
    private sealed class UnlistableGuideSource : Humans.Application.Interfaces.IGuideContentSource
    {
        public Task<string> GetMarkdownAsync(string fileStem, CancellationToken cancellationToken = default) =>
            throw new Octokit.NotFoundException("missing", System.Net.HttpStatusCode.NotFound);

        public Task<string> GetMarkdownAsync(string folderPath, string fileStem, CancellationToken cancellationToken = default) =>
            throw new Octokit.NotFoundException("missing", System.Net.HttpStatusCode.NotFound);

        public Task<IReadOnlyList<string>> ListMarkdownStemsAsync(string folderPath, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("github unreachable");
    }

    private static AgentToolDispatcher MakeDispatcher(
        Humans.AuditLog.Contracts.IAuditViewerService? auditViewer = null,
        Humans.Shifts.Contracts.IShiftView? shiftView = null,
        Humans.Shifts.Contracts.IBurnSettingsService? burnSettings = null,
        Humans.Application.Interfaces.IGuideContentSource? source = null)
    {
        var cache = new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions());
        source ??= new StubGuideSource();
        var sections = new AgentSectionDocReader(
            source, cache,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<
                AgentSectionDocReader>.Instance);
        var features = new AgentFeatureSpecReader(
            source, cache,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<
                AgentFeatureSpecReader>.Instance);
        var community = new CommunityFaqReader(
            source, cache,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<
                CommunityFaqReader>.Instance);
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<AgentToolDispatcher>.Instance;
        return new AgentToolDispatcher(
            sections,
            features,
            community,
            auditViewer ?? new StubAuditViewer(),
            shiftView ?? Substitute.For<Humans.Shifts.Contracts.IShiftView>(),
            burnSettings ?? Substitute.For<Humans.Shifts.Contracts.IBurnSettingsService>(),
            logger);
    }

    private sealed class StubGuideSource : Humans.Application.Interfaces.IGuideContentSource
    {
        /// <summary>The only feature specs this stub repo contains — anything else 404s like GitHub would.</summary>
        internal static readonly string[] FeatureStems = ["26-events", "gate-admissions"];

        public Task<string> GetMarkdownAsync(string fileStem, CancellationToken cancellationToken = default) =>
            Task.FromResult($"# {fileStem}");

        public Task<string> GetMarkdownAsync(string folderPath, string fileStem, CancellationToken cancellationToken = default)
        {
            if (string.Equals(folderPath, AgentFeatureSpecReader.FolderPath, StringComparison.Ordinal)
                && !FeatureStems.Contains(fileStem, StringComparer.Ordinal))
            {
                throw new Octokit.NotFoundException("missing", System.Net.HttpStatusCode.NotFound);
            }
            return Task.FromResult($"# {fileStem}\nLast updated: 2026-02-01\n\n## Overview\nStub.");
        }

        public Task<IReadOnlyList<string>> ListMarkdownStemsAsync(string folderPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<string>>(
                string.Equals(folderPath, CommunityFaqReader.FolderPath, StringComparison.Ordinal)
                    ? ["FAQ-general"]
                    : string.Equals(folderPath, AgentFeatureSpecReader.FolderPath, StringComparison.Ordinal)
                        ? FeatureStems
                        : []);
    }

    private static Humans.Shifts.Contracts.IShiftView MakeViewFor(
        Guid userId, IReadOnlyList<Humans.Shifts.Contracts.ShiftSignupSummary> signups)
    {
        var view = Substitute.For<Humans.Shifts.Contracts.IShiftView>();
        var record = ShiftFixtures.UserSummary(userId, signups);
        view.GetUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<Humans.Shifts.Contracts.ShiftUserSummary>(record));
        return view;
    }

    private sealed class StubAuditViewer : Humans.AuditLog.Contracts.IAuditViewerService
    {
        public IReadOnlyList<Humans.AuditLog.Contracts.AuditEvent> Events { get; init; } =
            [];

        /// <summary>Captures the limit value passed to <see cref="GetForUserAsync"/> for clamp-behaviour assertions.</summary>
        public int? LastLimit { get; private set; }

        public Task<IReadOnlyList<Humans.AuditLog.Contracts.AuditEvent>> GetRecentAsync(int count, CancellationToken ct = default) => Task.FromResult(Events);
        public Task<IReadOnlyList<Humans.AuditLog.Contracts.AuditEvent>> GetForUserAsync(Guid userId, int count, CancellationToken ct = default)
        {
            LastLimit = count;
            return Task.FromResult(Events);
        }
        public Task<IReadOnlyList<Humans.AuditLog.Contracts.AuditEvent>> GetForResourceAsync(Guid resourceId, CancellationToken ct = default) => Task.FromResult(Events);
        public Task<IReadOnlyList<Humans.AuditLog.Contracts.AuditEvent>> GetGoogleSyncForUserAsync(Guid userId, CancellationToken ct = default) => Task.FromResult(Events);
        public Task<Humans.AuditLog.Contracts.AuditEventPage> GetPageAsync(string? actionFilter, int page, int pageSize, CancellationToken ct = default) =>
            Task.FromResult(new Humans.AuditLog.Contracts.AuditEventPage(Events, Events.Count, 0));
        public Task<IReadOnlyList<Humans.AuditLog.Contracts.AuditEvent>> GetFilteredAsync(string? entityType, Guid? entityId, Guid? userId, IReadOnlyList<Humans.AuditLog.Contracts.AuditAction>? actions, int limit, CancellationToken ct = default) => Task.FromResult(Events);
    }

}
