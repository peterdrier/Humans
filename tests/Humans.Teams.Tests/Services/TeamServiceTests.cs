using Xunit;
using Humans.Base.Helpers;
using Humans.Base.Extensions;
// Tests seed TeamMember.User navs directly for DB-roundtrip verification.
// TeamMember.User is Obsolete per Â§6c and is never populated by production
// code (nobodies-collective/Humans#979 removed the in-memory stitcher); the
// tests set it on the entity before SaveChanges purely to satisfy the FK.
// File-wide disable cleared when tests switch to seeding via service calls
// instead of raw entity inserts.
#pragma warning disable CS0618
using AwesomeAssertions;
using Humans.Base.Caching;
using Humans.Base.Interfaces.Caching;
using Humans.AuditLog.Contracts;
using Humans.Auth.Contracts;
using Humans.Auth.Data;
using Humans.Auth.Services;
using Humans.Base.Constants;
using Humans.Base.Enums;
using Humans.Email.Contracts;
using Humans.EarlyEntry.Contracts;
using Humans.Notifications.Contracts;
using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Data;
using Humans.GoogleIntegration.Services;
using Humans.Settings.Contracts;
using Humans.Shifts.Contracts;
using Humans.Shifts.Data;
using Humans.Shifts.Domain;
using Humans.Shifts.Services;
using Humans.Teams.Contracts;
using Humans.Teams.Data;
using Humans.Teams.Domain;
using Humans.Teams.Services;
using Humans.Teams.Tests.Infrastructure;
using Humans.Users.Contracts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using RoleAssignmentService = Humans.Auth.Services.RoleAssignmentService;
using TeamService = Humans.Teams.Services.TeamService;

namespace Humans.Teams.Tests.Services;

public sealed class TeamServiceTests : TeamsTestHarness
{
    private readonly TeamService _service;
    private readonly TeamPageService _pages;
    private readonly IUserService _userService;
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly RoleAssignmentService _roleAssignmentService;
    private readonly ITeamResourceManagement _teamResourceService;
    private readonly IGoogleSyncWrite _googleSyncService = Substitute.For<IGoogleSyncWrite>();
    private readonly IGoogleIntegrationService _googleIdentities = Substitute.For<IGoogleIntegrationService>();
    private readonly ISystemTeamSync _systemTeamSync = Substitute.For<ISystemTeamSync>();

    public TeamServiceTests()
    {
        _roleAssignmentService = new RoleAssignmentService(
            new RoleAssignmentRepository(AuthDbFactory),
            Substitute.For<IUserService>(),
            AuditLog,
            Notifier,
            Substitute.For<ISystemTeamSync>(),
            Substitute.For<IRoleAssignmentClaimsCacheInvalidator>(),
            Substitute.For<IRoleAssignmentCacheInvalidator>(),
            Clock,
            NullLogger<RoleAssignmentService>.Instance);
        _teamResourceService = Substitute.For<ITeamResourceManagement>();
        _teamResourceService
            .GetTeamResourceSummariesAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, TeamResourceSummary>());

        // Capture the user service to a local before threading it into the locator
        // builder â€” NSubstitute can't attach an outer .Returns() to a factory that
        // itself configures substitute calls.
        var userService = _userService = NewDbBackedUserService();
        var googleOutboxService = new GoogleSyncOutboxService(
            new GoogleSyncOutboxRepository(GoogleIntegrationDbFactory));
        var serviceProvider = new ServiceLocatorBuilder()
            .With<ITeamManagement>()
            .With<IRoleAssignmentService>(_roleAssignmentService)
            .With(_emailService)
            .With(TestTeamsEmails.Create())
            .With(_systemTeamSync)
            .With(_googleSyncService)
            .With<IGoogleSyncOutboxService>(googleOutboxService)
            .With(_googleIdentities)
            .With<ITeamResourceService>(_teamResourceService)
            .With(userService)
            .With(userService)
            .Build();
        var shiftManagementService = new ShiftManagementService(
            new ShiftRepository(ShiftsDbFactory, ShiftsDb, Clock),
            AuditLog,
            AdminAuthorization,
            serviceProvider,
            Cache,
            Substitute.For<IShiftViewInvalidator>(),
            new EventCalendarResolver(NewSettingsServiceBackedByShiftsDb()),
            Clock,
            NullLogger<ShiftManagementService>.Instance);

        // Shift-auth invalidation: production path uses IShiftAuthorizationInvalidator
        // (backed by ShiftManagementService in DI). In tests we redirect it to the
        // same IMemoryCache entry so legacy assertions on the ShiftAuthorization
        // cache key keep working.
        ShiftAuthInvalidator
            .When(s => s.Invalidate(Arg.Any<Guid>()))
            .Do(ci => Cache.Remove(CacheKeys.ShiftAuthorization(ci.Arg<Guid>())));

        _service = new TeamService(
            new TeamRepository(TeamsDbFactory),
            AuditLog,
            Notifier,
            shiftManagementService,
            ShiftAuthInvalidator,
            AdminAuthorization,
            serviceProvider,
            Clock,
            NullLogger<TeamService>.Instance);
        _pages = new TeamPageService(
            _service, _teamResourceService, shiftManagementService,
            NewSettingsServiceBackedByShiftsDb(), _userService, _roleAssignmentService);
    }

    // IsUserAdminAsync / IsUserBoardMemberAsync coverage lives in RoleAssignmentServiceTests.

    // ==========================================================================
    // CreateTeamAsync
    // ==========================================================================

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContributeForUserAsync_IncludesArchivedMembershipHistory(bool requestArchivedId)
    {
        var survivor = SeedUser();
        var archived = SeedUser();
        var unrelated = SeedUser();
        var currentTeam = SeedTeam("Current team");
        var previousTeam = SeedTeam("Previous team");
        var unrelatedTeam = SeedTeam("Unrelated team");
        SeedTeamMember(currentTeam.Id, survivor.Id);
        var previous = SeedTeamMember(previousTeam.Id, archived.Id, leftAt: Clock.GetCurrentInstant());
        SeedTeamMember(unrelatedTeam.Id, unrelated.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);
        TeamsDb.ChangeTracker.Clear();
        var info = (await _userService.GetUserInfoAsync(survivor.Id, TestContext.Current.CancellationToken))!
            with
        { MergedUserIds = [archived.Id] };
        _userService.GetUserInfoAsync(survivor.Id, Arg.Any<CancellationToken>()).Returns(info);
        _userService.GetUserInfoAsync(archived.Id, Arg.Any<CancellationToken>()).Returns(info);

        var slices = await _service.ContributeForUserAsync(
            requestArchivedId ? archived.Id : survivor.Id, TestContext.Current.CancellationToken);

        var rows = System.Text.Json.JsonSerializer.SerializeToElement(slices.Single(s =>
                string.Equals(s.SectionName, TeamService.TeamMemberships, StringComparison.Ordinal)).Data)
            .EnumerateArray().ToArray();
        rows.Select(r => r.GetProperty("TeamName").GetString())
            .Should().BeEquivalentTo(["Current team", "Previous team"]);
        rows.Single(r => string.Equals(r.GetProperty("TeamName").GetString(), "Previous team", StringComparison.Ordinal))
            .GetProperty("LeftAt").GetString().Should().Be(previous.LeftAt.ToIso8601());
    }

    [HumansFact]
    public async Task ContributeForUserAsync_IncludesJoinRequestReviewAndOrderedStateHistory()
    {
        var team = SeedTeam("Export team");
        var user = SeedUser();
        var now = Clock.GetCurrentInstant();
        var request = new TeamJoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            UserId = user.Id,
            Status = TeamJoinRequestStatus.Rejected,
            RequestedAt = now - Duration.FromHours(1),
            ResolvedAt = now,
            Message = "Please let me join",
            ReviewNotes = "No vacancies"
        };
        request.StateHistory.Add(new TeamJoinRequestStateHistory
        {
            Id = Guid.NewGuid(),
            TeamJoinRequestId = request.Id,
            Status = TeamJoinRequestStatus.Rejected,
            ChangedAt = now,
            ChangedByUserId = Guid.NewGuid(),
            Notes = "No vacancies"
        });
        request.StateHistory.Add(new TeamJoinRequestStateHistory
        {
            Id = Guid.NewGuid(),
            TeamJoinRequestId = request.Id,
            Status = TeamJoinRequestStatus.Pending,
            ChangedAt = request.RequestedAt,
            ChangedByUserId = user.Id,
            Notes = "Requested membership"
        });
        TeamsDb.TeamJoinRequests.Add(request);
        TeamsDb.TeamJoinRequests.Add(new TeamJoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            UserId = Guid.NewGuid(),
            RequestedAt = now,
            Message = "Someone else's request"
        });
        await SaveAllAsync(TestContext.Current.CancellationToken);
        TeamsDb.ChangeTracker.Clear();

        var slices = await _service.ContributeForUserAsync(user.Id, TestContext.Current.CancellationToken);
        var slice = slices.Single(x => string.Equals(x.SectionName, "TeamJoinRequests", StringComparison.Ordinal));
        var exported = System.Text.Json.JsonSerializer.SerializeToElement(slice.Data).EnumerateArray().Single();
        exported.GetProperty("Message").GetString().Should().Be(request.Message);
        exported.GetProperty("ReviewNotes").GetString().Should().Be("No vacancies");
        var history = exported.GetProperty("StateHistory").EnumerateArray().ToList();
        history.Should().HaveCount(2);
        history.Select(x => x.GetProperty("Notes").GetString()).Should().Equal("Requested membership", "No vacancies");
        history[0].GetProperty("ChangedAt").GetString().Should().Be(request.RequestedAt.ToIso8601());
    }

    [HumansFact]
    public async Task CreateTeamAsync_ReservedSlug_Throws()
    {
        var act = () => _service.CreateTeamAsync("Roster", null, requiresApproval: false, cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*reserved route*");
    }

    [HumansFact]
    public async Task CreateTeamAsync_ParentNotFound_Throws()
    {
        var act = () => _service.CreateTeamAsync(
            "Child", null, requiresApproval: false, parentTeamId: Guid.NewGuid(), cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [HumansFact]
    public async Task CreateTeamAsync_ParentIsSystemTeam_Throws()
    {
        var parent = SeedTeam("Volunteers", type: SystemTeamType.Volunteers);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var act = () => _service.CreateTeamAsync(
            "Child", null, requiresApproval: false, parentTeamId: parent.Id, cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*System teams cannot be parents*");
    }

    [HumansFact]
    public async Task CreateTeamAsync_ParentAlreadyHasParent_Throws()
    {
        var grandparent = SeedTeam("Department");
        var parent = SeedTeam("SubTeam");
        parent.ParentTeamId = grandparent.Id;
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var act = () => _service.CreateTeamAsync(
            "GrandChild", null, requiresApproval: false, parentTeamId: parent.Id, cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already has a parent*");
    }

    [HumansFact]
    public async Task CreateTeamAsync_PersistsAllFields()
    {
        var result = await _service.CreateTeamAsync(
            name: "Engineering",
            description: "Builds things",
            requiresApproval: true,
            parentTeamId: null,
            googleGroupPrefix: "eng",
            isHidden: true, cancellationToken: TestContext.Current.CancellationToken);

        ClearAllTrackers();
        var stored = await TeamsDb.Teams.AsNoTracking().SingleAsync(t => t.Id == result.Id, TestContext.Current.CancellationToken);
        stored.Name.Should().Be("Engineering");
        stored.Description.Should().Be("Builds things");
        stored.Slug.Should().Be("engineering");
        stored.IsActive.Should().BeTrue();
        stored.RequiresApproval.Should().BeTrue();
        stored.IsHidden.Should().BeTrue();
        stored.GoogleGroupPrefix.Should().Be("eng");
        stored.ParentTeamId.Should().BeNull();
        stored.SystemTeamType.Should().Be(SystemTeamType.None);
        stored.CreatedAt.Should().Be(Clock.GetCurrentInstant());
        stored.UpdatedAt.Should().Be(Clock.GetCurrentInstant());
    }

    [HumansFact]
    public async Task CreateTeamAsync_ValidParent_PersistsParentId()
    {
        var parent = SeedTeam("Department");
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.CreateTeamAsync(
            "SubTeam", null, requiresApproval: false, parentTeamId: parent.Id, cancellationToken: TestContext.Current.CancellationToken);

        ClearAllTrackers();
        var stored = await TeamsDb.Teams.AsNoTracking().SingleAsync(t => t.Id == result.Id, TestContext.Current.CancellationToken);
        stored.ParentTeamId.Should().Be(parent.Id);
    }

    [HumansTheory]
    [InlineData("🔥🔥")]
    [InlineData("日本のチーム")]
    public async Task CreateTeamAsync_NonAsciiNamesGetDistinctUsableSlugs(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        var first = await _service.CreateTeamAsync(name, null, false, cancellationToken: ct);
        var second = await _service.CreateTeamAsync(name, null, false, cancellationToken: ct);

        foreach (var team in new[] { first, second })
        {
            SlugHelper.IsValidKebabSlug(team.Slug, maxLength: 256).Should().BeTrue();
            var read = await _service.GetTeamBySlugAsync(team.Slug, ct);
            read!.Id.Should().Be(team.Id);
            read.Name.Should().Be(name);
        }
        first.Slug.Should().NotBe(second.Slug);
    }

    [HumansFact]
    public async Task CreateTeamAsync_LongNameCollisionsStayWithinTheSlugColumn()
    {
        var ct = TestContext.Current.CancellationToken;
        var maxLength = TeamsDb.Model.FindEntityType(typeof(Team))!.FindProperty(nameof(Team.Slug))!.GetMaxLength()!.Value;
        var name = new string('a', maxLength - 3) + "-bc";
        var slugs = new HashSet<string>(StringComparer.Ordinal);
        for (var i = 0; i < 3; i++)
        {
            var team = await _service.CreateTeamAsync(name, null, false, cancellationToken: ct);

            SlugHelper.IsValidKebabSlug(team.Slug, maxLength).Should().BeTrue();
            slugs.Add(team.Slug).Should().BeTrue();
            (await _service.GetTeamBySlugAsync(team.Slug, ct))!.Name.Should().Be(name);
        }
    }

    [HumansTheory]
    [InlineData("🔥🔥")]
    [InlineData("日本のチーム")]
    public async Task UpdateTeamAsync_NonAsciiRenamePreservesTheUsableSlug(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        var team = SeedTeam("Original Name");
        await SaveAllAsync(ct);
        var originalSlug = team.Slug;

        await _service.UpdateTeamAsync(team.Id, name, null, false, true, cancellationToken: ct);

        var read = await _service.GetTeamBySlugAsync(originalSlug, ct);
        read.Should().NotBeNull();
        read.Name.Should().Be(name);
        read.Slug.Should().Be(originalSlug);
    }

    [HumansFact]
    public async Task CreateTeamAsync_SlugCollision_AppendsSuffix()
    {
        var first = await _service.CreateTeamAsync("Alpha", null, requiresApproval: false, cancellationToken: TestContext.Current.CancellationToken);
        first.Slug.Should().Be("alpha");

        var second = await _service.CreateTeamAsync("Alpha", null, requiresApproval: false, cancellationToken: TestContext.Current.CancellationToken);

        second.Slug.Should().Be("alpha-2");
    }

    // ==========================================================================
    // AddSeededMemberAsync
    // ==========================================================================

    [HumansFact]
    public async Task AddSeededMemberAsync_TeamNotFound_Throws()
    {
        var act = () => _service.AddSeededMemberAsync(
            Guid.NewGuid(), Guid.NewGuid(), TeamMemberRole.Member, Clock.GetCurrentInstant(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [HumansFact]
    public async Task AddSeededMemberAsync_SystemTeam_Throws()
    {
        var team = SeedTeam("Volunteers", type: SystemTeamType.Volunteers);
        var user = SeedUser();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var act = () => _service.AddSeededMemberAsync(
            team.Id, user.Id, TeamMemberRole.Member, Clock.GetCurrentInstant(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*cannot target system teams*");
    }

    [HumansFact]
    public async Task AddSeededMemberAsync_AlreadyMember_Throws()
    {
        var team = SeedTeam("Alpha");
        var user = SeedUser();
        SeedTeamMember(team.Id, user.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var act = () => _service.AddSeededMemberAsync(
            team.Id, user.Id, TeamMemberRole.Member, Clock.GetCurrentInstant(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already a member*");
    }

    [HumansFact]
    public async Task AddSeededMemberAsync_PersistsMemberWithRoleAndJoinedAt()
    {
        var team = SeedTeam("Alpha");
        var user = SeedUser();
        await SaveAllAsync(TestContext.Current.CancellationToken);
        var joinedAt = Instant.FromUtc(2025, 1, 1, 0, 0);

        var result = await _service.AddSeededMemberAsync(
            team.Id, user.Id, TeamMemberRole.Coordinator, joinedAt, TestContext.Current.CancellationToken);

        ClearAllTrackers();
        var stored = await TeamsDb.TeamMembers.AsNoTracking()
            .SingleAsync(m => m.TeamId == team.Id && m.UserId == user.Id, TestContext.Current.CancellationToken);
        stored.Role.Should().Be(TeamMemberRole.Coordinator);
        stored.JoinedAt.Should().Be(joinedAt);
        stored.LeftAt.Should().BeNull();
        result.Id.Should().Be(stored.Id);
    }

    // ==========================================================================
    // IsUserCoordinatorOfTeamAsync
    // ==========================================================================

    [HumansFact]
    public async Task IsUserCoordinatorOfTeamAsync_ActiveCoordinator_ReturnsTrue()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id, TeamMemberRole.Coordinator);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.IsUserCoordinatorOfTeamAsync(team.Id, user.Id, TestContext.Current.CancellationToken);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task IsUserCoordinatorOfTeamAsync_MemberNotCoordinator_ReturnsFalse()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.IsUserCoordinatorOfTeamAsync(team.Id, user.Id, TestContext.Current.CancellationToken);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task IsUserCoordinatorOfTeamAsync_LeftTeam_ReturnsFalse()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id, TeamMemberRole.Coordinator,
            leftAt: Clock.GetCurrentInstant() - Duration.FromDays(1));
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.IsUserCoordinatorOfTeamAsync(team.Id, user.Id, TestContext.Current.CancellationToken);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task IsUserCoordinatorOfTeamAsync_CoordinatorOfDifferentTeam_ReturnsFalse()
    {
        var user = SeedUser();
        var teamA = SeedTeam("Alpha");
        var teamB = SeedTeam("Beta");
        SeedTeamMember(teamA.Id, user.Id, TeamMemberRole.Coordinator);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.IsUserCoordinatorOfTeamAsync(teamB.Id, user.Id, TestContext.Current.CancellationToken);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task IsUserCoordinatorOfTeamAsync_CoordinatorOfParentTeam_ReturnsTrueForChildTeam()
    {
        var user = SeedUser();
        var parent = SeedTeam("Department");
        var child = SeedTeam("SubTeam");
        child.ParentTeamId = parent.Id;
        SeedTeamMember(parent.Id, user.Id, TeamMemberRole.Coordinator);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.IsUserCoordinatorOfTeamAsync(child.Id, user.Id, TestContext.Current.CancellationToken);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task IsUserCoordinatorOfTeamAsync_MemberOfParentTeam_NotCoordinator_ReturnsFalseForChildTeam()
    {
        var user = SeedUser();
        var parent = SeedTeam("Department");
        var child = SeedTeam("SubTeam");
        child.ParentTeamId = parent.Id;
        SeedTeamMember(parent.Id, user.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.IsUserCoordinatorOfTeamAsync(child.Id, user.Id, TestContext.Current.CancellationToken);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task IsUserCoordinatorOfTeamAsync_SubTeamManager_ReturnsTrue_ForOwnSubTeam()
    {
        var user = SeedUser();
        var parent = SeedTeam("Department");
        var child = SeedTeam("SubTeam");
        child.ParentTeamId = parent.Id;
        var member = SeedTeamMember(child.Id, user.Id, TeamMemberRole.Coordinator);
        var roleDef = SeedTeamRoleDefinition(child.Id, isManagement: true);
        SeedTeamRoleAssignment(roleDef.Id, member.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.IsUserCoordinatorOfTeamAsync(child.Id, user.Id, TestContext.Current.CancellationToken);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task IsUserCoordinatorOfTeamAsync_SubTeamManager_ReturnsFalse_ForSiblingSubTeam()
    {
        var user = SeedUser();
        var parent = SeedTeam("Department");
        var childA = SeedTeam("SubTeamA");
        childA.ParentTeamId = parent.Id;
        var childB = SeedTeam("SubTeamB");
        childB.ParentTeamId = parent.Id;
        var member = SeedTeamMember(childA.Id, user.Id, TeamMemberRole.Coordinator);
        var roleDef = SeedTeamRoleDefinition(childA.Id, isManagement: true);
        SeedTeamRoleAssignment(roleDef.Id, member.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.IsUserCoordinatorOfTeamAsync(childB.Id, user.Id, TestContext.Current.CancellationToken);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task IsUserCoordinatorOfTeamAsync_SubTeamManager_ReturnsFalse_ForParentDepartment()
    {
        var user = SeedUser();
        var parent = SeedTeam("Department");
        var child = SeedTeam("SubTeam");
        child.ParentTeamId = parent.Id;
        var member = SeedTeamMember(child.Id, user.Id, TeamMemberRole.Coordinator);
        var roleDef = SeedTeamRoleDefinition(child.Id, isManagement: true);
        SeedTeamRoleAssignment(roleDef.Id, member.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.IsUserCoordinatorOfTeamAsync(parent.Id, user.Id, TestContext.Current.CancellationToken);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task UpdateTeamPageContentWithResultAsync_NormalizesCallsToActionAndUpdatesPage()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.UpdateTeamPageContentAsync(
            team.Id,
            "Welcome", [
                new TeamPageCallToActionInput(" Join ", " /join ", CallToActionStyle.Primary),
                new TeamPageCallToActionInput("", "/ignored", CallToActionStyle.Secondary)
            ],
            isPublicPage: true,
            showCoordinatorsOnPublicPage: true,
            user.Id, cancellationToken: TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();

        var stored = await TeamsDb.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id, TestContext.Current.CancellationToken);
        stored.PageContent.Should().Be("Welcome");
        stored.CallsToAction.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new CallToAction
            {
                Text = "Join",
                Url = "/join",
                Style = CallToActionStyle.Primary
            });
        stored.IsPublicPage.Should().BeTrue();
        stored.ShowCoordinatorsOnPublicPage.Should().BeTrue();
    }

    [HumansTheory]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("java\nscript:alert(1)", false)]
    [InlineData("data:text/html,hello", false)]
    [InlineData("file:///etc/passwd", false)]
    [InlineData("https://example.org/join", true)]
    [InlineData("mailto:team@example.org", true)]
    [InlineData("/join", true)]
    [InlineData("../join#details", true)]
    public async Task UpdateTeamPageContentAsync_RejectsUnsafeActionUrlsWithoutChangingPage(string url, bool safe)
    {
        var team = SeedTeam("Alpha");
        team.PageContent = "Original";
        team.CallsToAction = [new CallToAction { Text = "Original", Url = "/original" }];
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.UpdateTeamPageContentAsync(team.Id, "Changed",
            [new TeamPageCallToActionInput("Join", url, CallToActionStyle.Primary)], true, false,
            Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Succeeded.Should().Be(safe);
        var stored = await TeamsDb.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id,
            TestContext.Current.CancellationToken);
        stored.PageContent.Should().Be(safe ? "Changed" : "Original");
        stored.CallsToAction.Should().ContainSingle().Which.Url.Should().Be(safe ? url : "/original");
        if (!safe) result.ErrorMessage.Should().Contain("URL");
    }

    [HumansTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task UpdateTeamPageContentAsync_DependencyFailure_DoesNotExposeDiagnostics(
        bool duringWrite, bool argumentFailure)
    {
        var repo = Substitute.For<ITeamRepository>();
        var team = SeedTeam("Alpha");
        Exception failure = argumentFailure
            ? new ArgumentException("Internal persistence details", nameof(duringWrite))
            : new InvalidOperationException("Internal persistence details");
        if (duringWrite)
        {
            repo.FindForMutationAsync(team.Id, Arg.Any<CancellationToken>()).Returns(team);
            repo.UpdateTeamAsync(Arg.Any<Team>(), Arg.Any<CancellationToken>())
                .Returns(Task.FromException<TeamUpdateConflict>(failure));
        }
        else
        {
            repo.FindForMutationAsync(team.Id, Arg.Any<CancellationToken>())
                .Returns(Task.FromException<Team?>(failure));
        }
        var service = new TeamService(
            repo, Substitute.For<IAuditLogService>(), Notifier,
            Substitute.For<IShiftService>(),
            ShiftAuthInvalidator,
            AdminAuthorization,
            new ServiceLocatorBuilder().Build(), Clock, NullLogger<TeamService>.Instance);

        var result = await service.UpdateTeamPageContentAsync(
            team.Id, "Welcome", [], true, false, Guid.NewGuid(),
            TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().BeNull();
    }

    [HumansTheory]
    [InlineData("missing", "not found")]
    [InlineData("too-many", "A team can have at most 3 calls to action.")]
    [InlineData("two-primary", "Only one primary call to action is allowed.")]
    [InlineData("system", "Only departments (non-system, top-level teams) can be made public.")]
    public async Task UpdateTeamPageContentAsync_KnownRefusal_RetainsFeedback(string refusal, string message)
    {
        var team = SeedTeam("Alpha", string.Equals(refusal, "system", StringComparison.Ordinal) ? SystemTeamType.Volunteers : SystemTeamType.None);
        await SaveAllAsync(TestContext.Current.CancellationToken);
        var actions = refusal switch
        {
            "too-many" => Enumerable.Range(0, 4)
                .Select(i => new TeamPageCallToActionInput($"Action {i}", "/join", CallToActionStyle.Secondary)).ToList(),
            "two-primary" => Enumerable.Range(0, 2)
                .Select(i => new TeamPageCallToActionInput($"Action {i}", "/join", CallToActionStyle.Primary)).ToList(),
            _ => new List<TeamPageCallToActionInput>()
        };

        var result = await _service.UpdateTeamPageContentAsync(
            string.Equals(refusal, "missing", StringComparison.Ordinal) ? Guid.NewGuid() : team.Id,
            "Welcome", actions, true, false, Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.ErrorMessage.Should().Contain(message);
    }

    // ==========================================================================
    // UpdateTeamAsync
    // ==========================================================================

    [HumansFact]
    public async Task UpdateTeamAsync_TeamNotFound_Refuses()
    {
        var act = () => _service.UpdateTeamAsync(
            Guid.NewGuid(), "name", null, requiresApproval: false, isActive: true, cancellationToken: TestContext.Current.CancellationToken);

        (await act()).ErrorMessage.Should().Match("*not found*");
    }

    [HumansFact]
    public async Task UpdateTeamAsync_SystemTeam_UpdatesOnlyDescriptionAndPrefixAndReturnsEarly()
    {
        var team = SeedTeam("Volunteers", type: SystemTeamType.Volunteers);
        team.Description = "old";
        team.RequiresApproval = false;
        await SaveAllAsync(TestContext.Current.CancellationToken);
        var originalName = team.Name;
        var originalSlug = team.Slug;

        var result = await _service.UpdateTeamAsync(
            team.Id,
            name: "Renamed",
            description: "new",
            requiresApproval: true,
            isActive: false,
            googleGroupPrefix: "vol-prefix", cancellationToken: TestContext.Current.CancellationToken);

        ClearAllTrackers();
        var stored = await TeamsDb.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id, TestContext.Current.CancellationToken);
        stored.Description.Should().Be("new");
        stored.GoogleGroupPrefix.Should().Be("vol-prefix");
        // System teams ignore name/requiresApproval/isActive on update
        stored.Name.Should().Be(originalName);
        stored.Slug.Should().Be(originalSlug);
        stored.RequiresApproval.Should().BeFalse();
        stored.IsActive.Should().BeTrue();
        result.Team!.Description.Should().Be("new");
    }

    [HumansFact]
    public async Task UpdateTeamAsync_ParentIsSelf_Refuses()
    {
        var team = SeedTeam("Alpha");
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var act = () => _service.UpdateTeamAsync(
            team.Id, "Alpha", null, false, true, parentTeamId: team.Id, cancellationToken: TestContext.Current.CancellationToken);

        (await act()).ErrorMessage.Should().Match("*own parent*");
    }

    [HumansFact]
    public async Task UpdateTeamAsync_TeamHasChildren_Refuses()
    {
        var parent = SeedTeam("Parent");
        var child = SeedTeam("Child");
        child.ParentTeamId = parent.Id;
        var newParent = SeedTeam("NewParent");
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var act = () => _service.UpdateTeamAsync(
            parent.Id, "Parent", null, false, true, parentTeamId: newParent.Id, cancellationToken: TestContext.Current.CancellationToken);

        (await act()).ErrorMessage.Should().Match("*sub-teams*");
    }

    [HumansFact]
    public async Task UpdateTeamAsync_ParentNotFound_Refuses()
    {
        var team = SeedTeam("Alpha");
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var act = () => _service.UpdateTeamAsync(
            team.Id, "Alpha", null, false, true, parentTeamId: Guid.NewGuid(), cancellationToken: TestContext.Current.CancellationToken);

        (await act()).ErrorMessage.Should().Match("*Parent team*not found*");
    }

    [HumansFact]
    public async Task UpdateTeamAsync_ParentIsSystemTeam_Refuses()
    {
        var team = SeedTeam("Alpha");
        var systemParent = SeedTeam("Volunteers", type: SystemTeamType.Volunteers);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var act = () => _service.UpdateTeamAsync(
            team.Id, "Alpha", null, false, true, parentTeamId: systemParent.Id, cancellationToken: TestContext.Current.CancellationToken);

        (await act()).ErrorMessage.Should().Match("*System teams cannot be parents*");
    }

    [HumansFact]
    public async Task UpdateTeamAsync_ParentAlreadyHasParent_Refuses()
    {
        var grandparent = SeedTeam("Grand");
        var parent = SeedTeam("Parent");
        parent.ParentTeamId = grandparent.Id;
        var team = SeedTeam("Alpha");
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var act = () => _service.UpdateTeamAsync(
            team.Id, "Alpha", null, false, true, parentTeamId: parent.Id, cancellationToken: TestContext.Current.CancellationToken);

        (await act()).ErrorMessage.Should().Match("*nest more than one level*");
    }

    [HumansFact]
    public async Task UpdateTeamAsync_InvalidCustomSlug_Refuses()
    {
        var team = SeedTeam("Alpha");
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var act = () => _service.UpdateTeamAsync(
            team.Id, "Alpha", null, false, true, customSlug: "!@#$", cancellationToken: TestContext.Current.CancellationToken);

        (await act()).ErrorMessage.Should().Match("*Custom slug is not valid*");
    }

    [HumansFact]
    public async Task UpdateTeamAsync_CustomSlugTakenByAnotherTeam_Refuses()
    {
        SeedTeam("Other Team");
        var team = SeedTeam("Alpha");
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var act = () => _service.UpdateTeamAsync(
            team.Id, "Alpha", null, false, true, customSlug: "other-team", cancellationToken: TestContext.Current.CancellationToken);

        (await act()).ErrorMessage.Should().Match("*already in use*");
    }

    [HumansFact]
    public async Task UpdateTeamAsync_BlankCustomSlug_NormalizesToNull()
    {
        var team = SeedTeam("Alpha");
        team.CustomSlug = "previous-custom";
        await SaveAllAsync(TestContext.Current.CancellationToken);

        await _service.UpdateTeamAsync(
            team.Id, "Alpha", null, false, true, customSlug: "", cancellationToken: TestContext.Current.CancellationToken);

        ClearAllTrackers();
        var stored = await TeamsDb.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id, TestContext.Current.CancellationToken);
        stored.CustomSlug.Should().BeNull();
    }

    [HumansTheory]
    [InlineData("Roster")]
    [InlineData("Birthdays")]
    [InlineData("Map")]
    [InlineData("My")]
    [InlineData("Sync")]
    [InlineData("Summary")]
    [InlineData("Create")]
    [InlineData("Search")]
    [InlineData("Tree")]
    public async Task UpdateTeamAsync_ReservedRenameKeepsTheExistingUrl(string name)
    {
        var ct = TestContext.Current.CancellationToken;
        var team = SeedTeam("Original Name");
        await SaveAllAsync(ct);
        var originalSlug = team.Slug;

        await _service.UpdateTeamAsync(team.Id, name, null, false, true, cancellationToken: ct);

        var read = await _service.GetTeamBySlugAsync(originalSlug, ct);
        read.Should().NotBeNull();
        read.Name.Should().Be(name);
        read.Slug.Should().Be(originalSlug);
    }

    [HumansTheory]
    [InlineData("Roster")]
    [InlineData("Birthdays")]
    [InlineData("Map")]
    [InlineData("My")]
    [InlineData("Sync")]
    [InlineData("Summary")]
    [InlineData("Create")]
    [InlineData("Search")]
    [InlineData("Tree")]
    public async Task UpdateTeamAsync_ReservedCustomSlugRefusesTheUpdate(string slug)
    {
        var ct = TestContext.Current.CancellationToken;
        var team = SeedTeam("Original Name");
        await SaveAllAsync(ct);
        var originalSlug = team.Slug;

        var act = () => _service.UpdateTeamAsync(team.Id, "Changed Name", null, false, false,
            customSlug: slug, isHidden: true, cancellationToken: ct);

        (await act()).ErrorMessage.Should().Match("*reserved route*");
        ClearAllTrackers();
        var stored = await TeamsDb.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id, ct);
        stored.Name.Should().Be("Original Name");
        stored.Slug.Should().Be(originalSlug);
        stored.CustomSlug.Should().BeNull();
        stored.IsActive.Should().BeTrue();
        stored.IsHidden.Should().BeFalse();
    }

    [HumansFact]
    public async Task UpdateTeamAsync_RenamesTeam_AndRegeneratesSlug()
    {
        var team = SeedTeam("Old Name");
        await SaveAllAsync(TestContext.Current.CancellationToken);

        await _service.UpdateTeamAsync(
            team.Id, "Brand New Name", null, false, true, cancellationToken: TestContext.Current.CancellationToken);

        ClearAllTrackers();
        var stored = await TeamsDb.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id, TestContext.Current.CancellationToken);
        stored.Name.Should().Be("Brand New Name");
        stored.Slug.Should().Be("brand-new-name");
    }

    [HumansFact]
    public async Task UpdateTeamAsync_RenameWithSlugTaken_KeepsOldSlug()
    {
        SeedTeam("Conflict");
        var team = SeedTeam("Old Name");
        await SaveAllAsync(TestContext.Current.CancellationToken);
        var originalSlug = team.Slug;

        await _service.UpdateTeamAsync(
            team.Id, "Conflict", null, false, true, cancellationToken: TestContext.Current.CancellationToken);

        ClearAllTrackers();
        var stored = await TeamsDb.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id, TestContext.Current.CancellationToken);
        stored.Name.Should().Be("Conflict");
        stored.Slug.Should().Be(originalSlug);
    }

    [HumansFact]
    public async Task UpdateTeamAsync_PersistsAllCoreFields()
    {
        var team = SeedTeam("Alpha");
        team.Description = "old";
        team.RequiresApproval = false;
        team.IsActive = true;
        team.IsPublicPage = false;
        await SaveAllAsync(TestContext.Current.CancellationToken);

        await _service.UpdateTeamAsync(
            team.Id,
            "Alpha Renamed",
            "new description",
            requiresApproval: true,
            isActive: false,
            googleGroupPrefix: "alpha-prefix",
            hasBudget: true,
            isHidden: true,
            isSensitive: true,
            isPromotedToDirectory: true, cancellationToken: TestContext.Current.CancellationToken);

        ClearAllTrackers();
        var stored = await TeamsDb.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id, TestContext.Current.CancellationToken);
        stored.Description.Should().Be("new description");
        stored.RequiresApproval.Should().BeTrue();
        stored.IsActive.Should().BeFalse();
        stored.GoogleGroupPrefix.Should().Be("alpha-prefix");
        stored.HasBudget.Should().BeTrue();
        stored.IsHidden.Should().BeTrue();
        stored.IsSensitive.Should().BeTrue();
        stored.IsPromotedToDirectory.Should().BeTrue();
        stored.UpdatedAt.Should().Be(Clock.GetCurrentInstant());
    }

    [HumansFact]
    public async Task UpdateTeamAsync_OptionalFlagsNull_LeavesExistingValues()
    {
        var team = SeedTeam("Alpha");
        team.HasBudget = true;
        team.IsHidden = true;
        team.IsSensitive = true;
        team.IsPromotedToDirectory = true;
        await SaveAllAsync(TestContext.Current.CancellationToken);

        await _service.UpdateTeamAsync(
            team.Id, "Alpha", null, false, true,
            hasBudget: null, isHidden: null, isSensitive: null, isPromotedToDirectory: null, cancellationToken: TestContext.Current.CancellationToken);

        ClearAllTrackers();
        var stored = await TeamsDb.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id, TestContext.Current.CancellationToken);
        stored.HasBudget.Should().BeTrue();
        stored.IsHidden.Should().BeTrue();
        stored.IsSensitive.Should().BeTrue();
        stored.IsPromotedToDirectory.Should().BeTrue();
    }

    [HumansFact]
    public async Task UpdateTeamAsync_BecomingChild_ForcesPublicPageFalse()
    {
        var parent = SeedTeam("Parent");
        var team = SeedTeam("Alpha");
        team.IsPublicPage = true;
        team.ShowCoordinatorsOnPublicPage = true;
        await SaveAllAsync(TestContext.Current.CancellationToken);

        await _service.UpdateTeamAsync(
            team.Id, "Alpha", null, false, true, parentTeamId: parent.Id, cancellationToken: TestContext.Current.CancellationToken);

        ClearAllTrackers();
        var stored = await TeamsDb.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id, TestContext.Current.CancellationToken);
        stored.IsPublicPage.Should().BeFalse();
        stored.ShowCoordinatorsOnPublicPage.Should().BeFalse();
        stored.ParentTeamId.Should().Be(parent.Id);
    }

    [HumansFact]
    public async Task UpdateTeamAsync_ParentChange_InvalidatesShiftAuthForManagementUsers()
    {
        var parentOld = SeedTeam("Old Parent");
        var parentNew = SeedTeam("New Parent");
        var team = SeedTeam("Alpha");
        team.ParentTeamId = parentOld.Id;
        var manager = SeedUser(displayName: "Manager");
        var member = SeedTeamMember(team.Id, manager.Id, TeamMemberRole.Coordinator);
        var roleDef = SeedTeamRoleDefinition(team.Id, isManagement: true);
        SeedTeamRoleAssignment(roleDef.Id, member.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);
        Cache.Set(CacheKeys.ShiftAuthorization(manager.Id), new[] { team.Id });

        await _service.UpdateTeamAsync(
            team.Id, "Alpha", null, false, true, parentTeamId: parentNew.Id, cancellationToken: TestContext.Current.CancellationToken);

        Cache.TryGetValue(CacheKeys.ShiftAuthorization(manager.Id), out _).Should().BeFalse();
    }

    // health.md §4: IsSensitive is written only by a global Admin. The gate is the service's,
    // not the edit controller's, so a second caller cannot set the flag around it.

    [HumansFact]
    public async Task UpdateTeamAsync_IsSensitiveChangedByNonAdmin_ThrowsAndLeavesFlag()
    {
        var team = SeedTeam("Alpha");
        await SaveAllAsync(TestContext.Current.CancellationToken);
        AdminAuthorization.RequireCurrentUserIsAdminAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new UnauthorizedAccessException("A full Admin role is required.")));

        var act = () => _service.UpdateTeamAsync(
            team.Id, "Alpha", null, requiresApproval: false, isActive: true,
            isSensitive: true, cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
        ClearAllTrackers();
        var stored = await TeamsDb.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id, TestContext.Current.CancellationToken);
        stored.IsSensitive.Should().BeFalse();
    }

    [HumansFact]
    public async Task UpdateTeamAsync_IsSensitiveChangedByAdmin_Writes()
    {
        var team = SeedTeam("Alpha");
        await SaveAllAsync(TestContext.Current.CancellationToken);

        await _service.UpdateTeamAsync(
            team.Id, "Alpha", null, requiresApproval: false, isActive: true,
            isSensitive: true, cancellationToken: TestContext.Current.CancellationToken);

        ClearAllTrackers();
        var stored = await TeamsDb.Teams.AsNoTracking().SingleAsync(t => t.Id == team.Id, TestContext.Current.CancellationToken);
        stored.IsSensitive.Should().BeTrue();
    }

    [HumansFact]
    public async Task UpdateTeamAsync_IsSensitiveUnchanged_DoesNotRequireAdmin()
    {
        // The dev fixture seeders on ITeamSeeding pass the flag's current value; a no-op is not a write.
        var team = SeedTeam("Alpha");
        await SaveAllAsync(TestContext.Current.CancellationToken);
        AdminAuthorization.RequireCurrentUserIsAdminAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromException(new UnauthorizedAccessException("A full Admin role is required.")));

        await _service.UpdateTeamAsync(
            team.Id, "Alpha", null, requiresApproval: false, isActive: true,
            isSensitive: false, cancellationToken: TestContext.Current.CancellationToken);

        await AdminAuthorization.DidNotReceive().RequireCurrentUserIsAdminAsync(Arg.Any<CancellationToken>());
    }

    // ==========================================================================
    // Create/Update with Google group (T15 — service-owned compensation)
    // ==========================================================================

    [HumansFact]
    public async Task CreateTeamWithGoogleGroup_ProvisioningFails_ClearsPrefixAndWarns()
    {
        _googleSyncService.EnsureTeamGroupAsync(Arg.Any<Guid>(), cancellationToken: Arg.Any<CancellationToken>())
            .Returns<GroupLinkResult>(_ => throw new InvalidOperationException("Workspace down"));

        var result = await _service.CreateTeamWithGoogleGroupAsync(
            "Gate Crew", null, requiresApproval: false, googleGroupPrefix: "gate-crew",
            cancellationToken: TestContext.Current.CancellationToken);

        result.GroupWarning.Should().Contain("Google Group setup failed")
            .And.Contain("prefix has been cleared");
        ClearAllTrackers();
        var reloaded = await TeamsDb.Teams.SingleAsync(t => t.Id == result.Team.Id, TestContext.Current.CancellationToken);
        reloaded.GoogleGroupPrefix.Should().BeNull();
    }

    [HumansFact]
    public async Task CreateTeamWithGoogleGroup_NoPrefix_SkipsProvisioning()
    {
        var result = await _service.CreateTeamWithGoogleGroupAsync(
            "Quiet Crew", null, requiresApproval: false,
            cancellationToken: TestContext.Current.CancellationToken);

        result.GroupWarning.Should().BeNull();
        await _googleSyncService.DidNotReceive().EnsureTeamGroupAsync(
            Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task UpdateTeamWithGoogleGroup_GroupNeedsConfirmation_ReturnsWarning()
    {
        var team = SeedTeam("Alpha");
        await SaveAllAsync(TestContext.Current.CancellationToken);
        _googleSyncService.EnsureTeamGroupAsync(team.Id, cancellationToken: Arg.Any<CancellationToken>())
            .Returns(GroupLinkResult.NeedsConfirmation("Group was deactivated.", Guid.NewGuid()));

        var result = await _service.UpdateTeamWithGoogleGroupAsync(
            team.Id, team.Name, team.Description, team.RequiresApproval, team.IsActive,
            cancellationToken: TestContext.Current.CancellationToken);

        result.GroupWarning.Should().Be("Group was deactivated.");
    }

    [HumansFact]
    public async Task UpdateTeamWithGoogleGroup_GroupOk_NoWarning()
    {
        var team = SeedTeam("Alpha");
        await SaveAllAsync(TestContext.Current.CancellationToken);
        _googleSyncService.EnsureTeamGroupAsync(team.Id, cancellationToken: Arg.Any<CancellationToken>())
            .Returns(GroupLinkResult.Ok());

        var result = await _service.UpdateTeamWithGoogleGroupAsync(
            team.Id, team.Name, team.Description, team.RequiresApproval, team.IsActive,
            cancellationToken: TestContext.Current.CancellationToken);

        result.GroupWarning.Should().BeNull();
    }

    // ==========================================================================
    // JoinTeamAsync (T13 — dispatch on the team's join policy)
    // ==========================================================================

    [HumansFact]
    public async Task JoinTeamAsync_ApprovalRequiredTeam_CreatesPendingRequest()
    {
        var user = SeedUser();
        var team = SeedTeam("Gate", requiresApproval: true);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var outcome = await _service.JoinTeamAsync(team.Id, user.Id, "Pick me", TestContext.Current.CancellationToken);

        outcome.ErrorKey.Should().BeNull();
        outcome.Outcome.Should().Be(TeamJoinOutcome.RequestSubmitted);
        (await TeamsDb.TeamJoinRequests.SingleAsync(TestContext.Current.CancellationToken))
            .UserId.Should().Be(user.Id);
        (await TeamsDb.TeamMembers.AnyAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [HumansFact]
    public async Task JoinTeamAsync_ConcurrentRequests_CreateOnlyOnePendingRequest()
    {
        var ct = TestContext.Current.CancellationToken;
        var pause = new PauseFirstJoinRequestSave();
        var options = new DbContextOptionsBuilder<TeamsDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).AddInterceptors(pause).Options;
        var repository = new TeamRepository(new TestDbContextFactory<TeamsDbContext>(options));
        var team = new Team
        {
            Id = Guid.NewGuid(),
            Name = "Approval team",
            Slug = "approval-team",
            IsActive = true,
            RequiresApproval = true,
            SystemTeamType = SystemTeamType.None,
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        };
        await using (var seed = new TeamsDbContext(options))
        {
            seed.Teams.Add(team);
            await seed.SaveChangesAsync(ct);
        }
        var provider = new ServiceLocatorBuilder().Build();
        TeamService NewService() => new(repository, AuditLog, Notifier,
            Substitute.For<IShiftService>(),
            ShiftAuthInvalidator, AdminAuthorization, provider, Clock,
            NullLogger<TeamService>.Instance);
        var userId = Guid.NewGuid();
        var first = NewService().JoinTeamAsync(team.Id, userId, "Pick me", ct);
        Task<TeamJoinRequestResult> second;
        try
        {
            await Task.WhenAny(first, pause.Entered.Task).WaitAsync(TimeSpan.FromSeconds(10), ct);
            if (first.IsCompleted) await first;
            pause.Entered.Task.IsCompleted.Should().BeTrue("the first join request save must be paused");
            second = NewService().RequestToJoinTeamAsync(team.Id, userId, "Pick me", ct);
        }
        finally
        {
            pause.Resume.TrySetResult();
        }
        await Task.WhenAll(first, second);

        await using var db = new TeamsDbContext(options);
        var request = (await db.TeamJoinRequests.ToListAsync(ct)).Should().ContainSingle().Subject;
        request.TeamId.Should().Be(team.Id);
        request.UserId.Should().Be(userId);
        request.Status.Should().Be(TeamJoinRequestStatus.Pending);
        (await first).Outcome.Should().Be(TeamJoinOutcome.RequestSubmitted);
        (await second).ErrorKey.Should().Be("Teams_Team_AlreadyPendingRequest");
    }

    private sealed class PauseFirstJoinRequestSave : SaveChangesInterceptor
    {
        private int _paused;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context!.ChangeTracker.Entries<TeamJoinRequest>()
                    .Any(entry => entry.State == EntityState.Added)
                && Interlocked.CompareExchange(ref _paused, 1, 0) == 0)
            {
                Entered.TrySetResult();
                await Resume.Task.WaitAsync(cancellationToken);
            }
            return result;
        }
    }

    [HumansFact]
    public async Task JoinTeamAsync_OpenTeam_JoinsImmediately()
    {
        var user = SeedUser();
        var team = SeedTeam("Open Crew", requiresApproval: false);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var outcome = await _service.JoinTeamAsync(team.Id, user.Id, message: null, TestContext.Current.CancellationToken);

        outcome.ErrorKey.Should().BeNull();
        outcome.Outcome.Should().Be(TeamJoinOutcome.Joined);
        (await TeamsDb.TeamMembers.SingleAsync(TestContext.Current.CancellationToken))
            .UserId.Should().Be(user.Id);
        (await TeamsDb.TeamJoinRequests.AnyAsync(TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    // ==========================================================================
    // RequestToJoinTeamAsync
    // ==========================================================================

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JoinTeamAsync_DeactivatedTeamCannotBeRejoined(bool requiresApproval)
    {
        var user = SeedUser();
        var team = SeedTeam("Closed Team", requiresApproval: requiresApproval);
        SeedTeamMember(team.Id, user.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);
        await _service.DeleteTeamAsync(team.Id, Guid.NewGuid(), TestContext.Current.CancellationToken);

        var result = await _service.JoinTeamAsync(team.Id, user.Id, "Returning", TestContext.Current.CancellationToken);
        result.ErrorKey.Should().Be("Teams_NotFound");
        result.Outcome.Should().BeNull();

        var detail = await _pages.GetTeamDetailAsync(team.Slug, user.Id, TestContext.Current.CancellationToken);
        detail.Should().NotBeNull();
        detail.CanCurrentUserJoin.Should().BeFalse();
        (await TeamsDb.TeamMembers.AsNoTracking().CountAsync(m => m.TeamId == team.Id && m.LeftAt == null,
            TestContext.Current.CancellationToken)).Should().Be(0);
        (await TeamsDb.TeamJoinRequests.AsNoTracking().CountAsync(r => r.TeamId == team.Id,
            TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [HumansFact]
    public async Task RequestToJoinTeamAsync_TeamNotFound_ReturnsRefusal()
    {
        var user = SeedUser();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.RequestToJoinTeamAsync(Guid.NewGuid(), user.Id, null, TestContext.Current.CancellationToken);

        result.ErrorKey.Should().Be("Teams_NotFound");
        result.Request.Should().BeNull();
        await AuditLog.DidNotReceiveWithAnyArgs().LogAsync(default, default!, default, default!, default(Guid));
    }

    [HumansFact]
    public async Task RequestToJoinTeamAsync_SystemTeam_ReturnsRefusal()
    {
        var user = SeedUser();
        var team = SeedTeam("Volunteers", type: SystemTeamType.Volunteers, requiresApproval: true);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.RequestToJoinTeamAsync(team.Id, user.Id, null, TestContext.Current.CancellationToken);

        result.ErrorKey.Should().Be("Teams_Team_CannotJoinSystem");
        result.Request.Should().BeNull();
        await AuditLog.DidNotReceiveWithAnyArgs().LogAsync(default, default!, default, default!, default(Guid));
    }

    [HumansFact]
    public async Task RequestToJoinTeamAsync_HiddenTeam_ReturnsRefusal()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha", requiresApproval: true);
        team.IsHidden = true;
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.RequestToJoinTeamAsync(team.Id, user.Id, null, TestContext.Current.CancellationToken);

        result.ErrorKey.Should().Be("Teams_CannotJoinHidden");
        result.Request.Should().BeNull();
        await AuditLog.DidNotReceiveWithAnyArgs().LogAsync(default, default!, default, default!, default(Guid));
    }

    [HumansFact]
    public async Task RequestToJoinTeamAsync_TeamWithoutApproval_ReturnsRefusal()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.RequestToJoinTeamAsync(team.Id, user.Id, null, TestContext.Current.CancellationToken);

        result.ErrorKey.Should().Be("Teams_JoinPolicyChanged");
        result.Request.Should().BeNull();
        await AuditLog.DidNotReceiveWithAnyArgs().LogAsync(default, default!, default, default!, default(Guid));
    }

    [HumansFact]
    public async Task RequestToJoinTeamAsync_AlreadyHasPendingRequest_ReturnsRefusal()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha", requiresApproval: true);
        SeedJoinRequest(team.Id, user.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.RequestToJoinTeamAsync(team.Id, user.Id, null, TestContext.Current.CancellationToken);

        result.ErrorKey.Should().Be("Teams_Team_AlreadyPendingRequest");
        result.Request.Should().BeNull();
        await AuditLog.DidNotReceiveWithAnyArgs().LogAsync(default, default!, default, default!, default(Guid));
    }

    [HumansFact]
    public async Task RequestToJoinTeamAsync_AlreadyMember_ReturnsRefusal()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha", requiresApproval: true);
        SeedTeamMember(team.Id, user.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.RequestToJoinTeamAsync(team.Id, user.Id, null, TestContext.Current.CancellationToken);

        result.ErrorKey.Should().Be("Teams_Team_AlreadyMember");
        result.Request.Should().BeNull();
        await AuditLog.DidNotReceiveWithAnyArgs().LogAsync(default, default!, default, default!, default(Guid));
    }

    [HumansFact]
    public async Task RequestToJoinTeamAsync_HappyPath_PersistsRequestWithMessage()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha", requiresApproval: true);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var response = await _service.RequestToJoinTeamAsync(team.Id, user.Id, "Pick me", TestContext.Current.CancellationToken);

        response.ErrorKey.Should().BeNull();
        response.Request.Should().NotBeNull();
        var result = response.Request!;

        result.TeamId.Should().Be(team.Id);
        result.UserId.Should().Be(user.Id);
        result.Message.Should().Be("Pick me");
        result.Status.Should().Be(TeamJoinRequestStatus.Pending);
        result.RequestedAt.Should().Be(Clock.GetCurrentInstant());

        ClearAllTrackers();
        var stored = await TeamsDb.TeamJoinRequests.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        stored.Id.Should().Be(result.Id);
        stored.Message.Should().Be("Pick me");
    }

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task JoinTeam_NameLookupFailure_ReturnsCommittedRequestOrMembership(bool requiresApproval)
    {
        var user = SeedUser();
        var coordinator = SeedUser(displayName: "Coordinator");
        var team = SeedTeam("Alpha", requiresApproval: requiresApproval);
        SeedTeamMember(team.Id, coordinator.Id, TeamMemberRole.Coordinator);
        await SaveAllAsync(TestContext.Current.CancellationToken);
        var userInfo = await _userService.GetUserInfoAsync(
            user.Id, TestContext.Current.CancellationToken);
        var lookups = 0;
        _userService.GetUserInfoAsync(user.Id, Arg.Any<CancellationToken>())
            .Returns(_ => !requiresApproval && lookups++ == 0
                ? new ValueTask<UserInfo?>(userInfo)
                : ValueTask.FromException<UserInfo?>(new IOException("Name lookup failed")));

        if (requiresApproval)
        {
            var response = await _service.RequestToJoinTeamAsync(
                team.Id, user.Id, "Pick me", TestContext.Current.CancellationToken);
            response.ErrorKey.Should().BeNull();
            response.Request.Should().NotBeNull();
            var result = response.Request!;
            result.Status.Should().Be(TeamJoinRequestStatus.Pending);
            ClearAllTrackers();
            (await TeamsDb.TeamJoinRequests.AsNoTracking().SingleAsync(
                TestContext.Current.CancellationToken)).Id.Should().Be(result.Id);
        }
        else
        {
            var response = await _service.JoinTeamDirectlyAsync(
                team.Id, user.Id, TestContext.Current.CancellationToken);
            response.ErrorKey.Should().BeNull();
            response.Member.Should().NotBeNull();
            var result = response.Member!;
            result.UserId.Should().Be(user.Id);
            ClearAllTrackers();
            (await TeamsDb.TeamMembers.AsNoTracking().SingleAsync(
                m => m.UserId == user.Id, TestContext.Current.CancellationToken))
                .Id.Should().Be(result.Id);
            await AuditLog.Received(1).LogAsync(
                AuditAction.TeamJoinedDirectly, nameof(Team), team.Id,
                $"Joined {team.Name} directly", user.Id,
                relatedEntityId: user.Id, relatedEntityType: nameof(User));
        }
    }

    // ==========================================================================
    // RemoveMemberAsync
    // ==========================================================================

    [HumansFact]
    public async Task RemoveMemberAsync_TeamNotFound_ReturnsRefusal()
    {
        var actor = SeedUser(displayName: "Actor");
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.RemoveMemberAsync(Guid.NewGuid(), Guid.NewGuid(), actor.Id, TestContext.Current.CancellationToken);

        result.ErrorKey.Should().Be("Teams_RemoveMember_TeamMissing");
    }

    [HumansFact]
    public async Task RemoveMemberAsync_SystemTeam_ReturnsRefusal()
    {
        var actor = SeedUser(displayName: "Actor");
        var target = SeedUser(displayName: "Target");
        var team = SeedTeam("Volunteers", type: SystemTeamType.Volunteers);
        var member = SeedTeamMember(team.Id, target.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.RemoveMemberAsync(team.Id, target.Id, actor.Id, TestContext.Current.CancellationToken);

        result.ErrorKey.Should().Be("Teams_RemoveMember_SystemTeam");
        member.LeftAt.Should().BeNull();
    }

    [HumansFact]
    public async Task RemoveMemberAsync_ActorLacksPermission_ReturnsRefusal()
    {
        var actor = SeedUser(displayName: "Actor");
        var target = SeedUser(displayName: "Target");
        var team = SeedTeam("Alpha");
        var member = SeedTeamMember(team.Id, target.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.RemoveMemberAsync(team.Id, target.Id, actor.Id, TestContext.Current.CancellationToken);

        result.ErrorKey.Should().Be("Teams_RemoveMember_Permission");
        member.LeftAt.Should().BeNull();
    }

    [HumansFact]
    public async Task RemoveMemberAsync_TargetNotAMember_ReturnsRefusal()
    {
        var actor = SeedUser(displayName: "Actor");
        var target = SeedUser(displayName: "Target");
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, actor.Id, TeamMemberRole.Coordinator);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.RemoveMemberAsync(team.Id, target.Id, actor.Id, TestContext.Current.CancellationToken);

        result.ErrorKey.Should().Be("Teams_RemoveMember_NotMember");
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReassignAsync_RefusedRemovalStillAbortsMerge(bool targetAlreadyMember)
    {
        var actor = SeedUser(displayName: "Actor");
        var source = SeedUser(displayName: "Source");
        var target = SeedUser(displayName: "Target");
        var team = SeedTeam("Alpha");
        var member = SeedTeamMember(team.Id, source.Id);
        if (targetAlreadyMember) SeedTeamMember(team.Id, target.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        Func<Task> act = () => _service.ReassignAsync(source.Id, target.Id, actor.Id,
            Clock.GetCurrentInstant(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*permission*");
        member.LeftAt.Should().BeNull();
        (await TeamsDb.TeamMembers.CountAsync(m => m.TeamId == team.Id && m.UserId == target.Id,
            TestContext.Current.CancellationToken)).Should().Be(targetAlreadyMember ? 1 : 0);
    }

    [HumansFact]
    public async Task RemoveMemberAsync_RegularMember_SetsLeftAt_WithoutCoordinatorSync()
    {
        var actor = SeedUser(displayName: "Actor");
        var target = SeedUser(displayName: "Target");
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, actor.Id, TeamMemberRole.Coordinator);
        var member = SeedTeamMember(team.Id, target.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        await _service.RemoveMemberAsync(team.Id, target.Id, actor.Id, TestContext.Current.CancellationToken);

        ClearAllTrackers();
        var reloaded = await TeamsDb.TeamMembers.AsNoTracking().SingleAsync(tm => tm.Id == member.Id, TestContext.Current.CancellationToken);
        reloaded.LeftAt.Should().Be(Clock.GetCurrentInstant());
        await _systemTeamSync.DidNotReceive().SyncMembershipForUserAsync(
            Arg.Any<Guid>(), Arg.Any<SystemTeamType>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RemoveMemberAsync_Coordinator_SyncsCoordinatorsSystemTeam()
    {
        var actor = SeedUser(displayName: "Actor");
        SeedRoleAssignment(actor.Id, RoleNames.Admin,
            Clock.GetCurrentInstant() - Duration.FromDays(1));
        var target = SeedUser(displayName: "Target");
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, target.Id, TeamMemberRole.Coordinator);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        await _service.RemoveMemberAsync(team.Id, target.Id, actor.Id, TestContext.Current.CancellationToken);

        // The coordinator-eligibility reconcile is part of the mutation (T1).
        await _systemTeamSync.Received(1).SyncMembershipForUserAsync(
            target.Id, SystemTeamType.Coordinators, Arg.Any<CancellationToken>());
    }

    // ==========================================================================
    // RejectJoinRequestAsync
    // ==========================================================================

    [HumansFact]
    public async Task RejectJoinRequestAsync_RequestNotFound_Throws()
    {
        var approver = SeedUser(displayName: "Approver");
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var act = () => _service.RejectJoinRequestAsync(Guid.NewGuid(), approver.Id, "reason", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [HumansFact]
    public async Task RejectJoinRequestAsync_ApproverLacksPermission_Throws()
    {
        var stranger = SeedUser(displayName: "Stranger");
        var requester = SeedUser(displayName: "Requester");
        var team = SeedTeam("Alpha", requiresApproval: true);
        var request = SeedJoinRequest(team.Id, requester.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var act = () => _service.RejectJoinRequestAsync(request.Id, stranger.Id, "no thanks", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*permission*");
    }

    [HumansFact]
    public async Task RejectJoinRequestAsync_RequestAlreadyApproved_Throws()
    {
        var coordinator = SeedUser(displayName: "Coordinator");
        var requester = SeedUser(displayName: "Requester");
        var team = SeedTeam("Alpha", requiresApproval: true);
        SeedTeamMember(team.Id, coordinator.Id, TeamMemberRole.Coordinator);
        var request = SeedJoinRequest(team.Id, requester.Id, TeamJoinRequestStatus.Approved);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var act = () => _service.RejectJoinRequestAsync(request.Id, coordinator.Id, "late", TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*pending*");
    }

    [HumansFact]
    public async Task RejectJoinRequestAsync_HappyPath_RecordsRejectionWithReason()
    {
        var coordinator = SeedUser(displayName: "Coordinator");
        var requester = SeedUser(displayName: "Requester");
        var team = SeedTeam("Alpha", requiresApproval: true);
        SeedTeamMember(team.Id, coordinator.Id, TeamMemberRole.Coordinator);
        var request = SeedJoinRequest(team.Id, requester.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        await _service.RejectJoinRequestAsync(request.Id, coordinator.Id, "out of capacity", TestContext.Current.CancellationToken);

        ClearAllTrackers();
        var stored = await TeamsDb.TeamJoinRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id, TestContext.Current.CancellationToken);
        stored.Status.Should().Be(TeamJoinRequestStatus.Rejected);
        stored.ReviewNotes.Should().Be("out of capacity");
        stored.ReviewedByUserId.Should().Be(coordinator.Id);
        stored.ResolvedAt.Should().Be(Clock.GetCurrentInstant());
    }

    // ==========================================================================
    // ApproveJoinRequestAsync
    // ==========================================================================

    [HumansTheory]
    [InlineData("normal", "es", "es")]
    [InlineData("email-prep-failure", "es", "es")]
    [InlineData("profile-lookup-failure", "es", "en")]
    [InlineData("normal", "", "en")]
    [InlineData("normal", " ", "en")]
    [InlineData("normal", "not a culture!", "en")]
    [InlineData("normal", "fr-FR", "en")]
    public async Task AddedMemberNotice_ReusesEmailRecipientLanguageAndSurvivesPreparationFailure(
        string mode, string language, string expectedCulture)
    {
        using var actorCulture = new CultureScope("fr");
        var ct = TestContext.Current.CancellationToken;
        var coordinator = SeedUser(displayName: "Coordinator");
        var requester = SeedUser(displayName: "Requester");
        requester.PreferredLanguage = language;
        var team = SeedTeam("Alpha", requiresApproval: true);
        SeedTeamMember(team.Id, coordinator.Id, TeamMemberRole.Coordinator);
        var request = SeedJoinRequest(team.Id, requester.Id);
        await SaveAllAsync(ct);
        if (string.Equals(mode, "email-prep-failure", StringComparison.Ordinal))
            _teamResourceService.GetTeamResourcesAsync(team.Id, Arg.Any<CancellationToken>())
                .Returns(Task.FromException<IReadOnlyList<GoogleResourceSnapshot>>(new IOException("Email resource lookup unavailable")));
        if (string.Equals(mode, "profile-lookup-failure", StringComparison.Ordinal))
            _userService.GetUserInfosAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(requester.Id)), Arg.Any<CancellationToken>())
                .Returns(ValueTask.FromException<IReadOnlyDictionary<Guid, UserInfo>>(new IOException("Profile lookup unavailable")));

        await _service.ApproveJoinRequestAsync(request.Id, coordinator.Id, null, ct);

        var args = Notifier.ReceivedCalls().Where(call => call.GetArguments()[0] is NotificationSource source
                && source == NotificationSource.TeamMemberAdded
                && ((IReadOnlyList<Guid>)call.GetArguments()[4]!).Contains(requester.Id))
            .Single().GetArguments();
        if (string.Equals(expectedCulture, "en", StringComparison.Ordinal))
            ((string)args[3]!).Should().EndWith("added to Alpha");
        else
            args[3].Should().Be("Se le ha añadido al equipo Alpha");
        args[6].Should().Be($"/Teams/{team.Slug}");
        if (string.Equals(mode, "normal", StringComparison.Ordinal))
            await _emailService.Received(1).SendAsync(
                Arg.Is<EmailMessage>(m => m.RecipientEmail == requester.Email
                    && m.Subject.EndsWith($"#{expectedCulture}", StringComparison.Ordinal)),
                Arg.Any<CancellationToken>());
        await _userService.Received(1).GetUserInfosAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(requester.Id)), Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData("approve")]
    [InlineData("reject")]
    [InlineData("remove")]
    [InlineData("request")]
    [InlineData("join")]
    public async Task MemberNotices_LongNamesDispatchFullCopy(string action)
    {
        var coordinator = SeedUser(displayName: "Coordinator");
        var requester = SeedUser(displayName: "Requester");
        var name = new string('x', 168) + "😀" + new string('x', 86);
        var team = SeedTeam(name, requiresApproval: !string.Equals(action, "join", StringComparison.Ordinal));
        team.Slug = "alpha";
        SeedTeamMember(team.Id, coordinator.Id, TeamMemberRole.Coordinator);
        var request = SeedJoinRequest(team.Id, requester.Id);
        if (string.Equals(action, "remove", StringComparison.Ordinal))
            SeedTeamMember(team.Id, requester.Id);
        if (string.Equals(action, "request", StringComparison.Ordinal) || string.Equals(action, "join", StringComparison.Ordinal))
            TeamsDb.TeamJoinRequests.Remove(request);
        await SaveAllAsync(TestContext.Current.CancellationToken);
        switch (action)
        {
            case "approve": await _service.ApproveJoinRequestAsync(request.Id, coordinator.Id, null, TestContext.Current.CancellationToken); break;
            case "reject": await _service.RejectJoinRequestAsync(request.Id, coordinator.Id, "Reason", TestContext.Current.CancellationToken); break;
            case "remove": await _service.RemoveMemberAsync(team.Id, requester.Id, coordinator.Id, TestContext.Current.CancellationToken); break;
            default: await _service.JoinTeamAsync(team.Id, requester.Id, null, TestContext.Current.CancellationToken); break;
        }
        var notices = Notifier.ReceivedCalls().Where(call => call.GetArguments()[0] is NotificationSource)
            .Select(call => call.GetArguments()).ToList();
        (string Title, string? Body)[] expected = action switch
        {
            "approve" => [($"You've been added to {name}", null),
                ($"Your request to join {name} has been approved", $"Welcome to {name}!")],
            "reject" => [($"Your request to join {name} was not approved", $"Your request to join {name} was not approved.")],
            "remove" => [($"You were removed from {name}", null)],
            "request" => [($"New join request for {name}", $"Requester has requested to join {name}.")],
            _ => [($"You've been added to {name}", null), ($"Requester joined {name}", $"Requester has joined {name}.")]
        };
        notices.Select(args => ((string)args[3]!, (string?)args[5])).Should().BeEquivalentTo(expected);
    }

    [HumansTheory]
    [InlineData("approve")]
    [InlineData("reject")]
    [InlineData("remove")]
    [InlineData("lookup-failure")]
    public async Task MemberNotices_UseRecipientLanguageWithLookupFailureFallback(string action)
    {
        var coordinator = SeedUser(displayName: "Coordinator");
        var requester = SeedUser(displayName: "Requester");
        requester.PreferredLanguage = "es";
        var team = SeedTeam("Alpha", requiresApproval: true);
        SeedTeamMember(team.Id, coordinator.Id, TeamMemberRole.Coordinator);
        var request = SeedJoinRequest(team.Id, requester.Id);
        if (string.Equals(action, "remove", StringComparison.Ordinal))
            SeedTeamMember(team.Id, requester.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);
        if (string.Equals(action, "lookup-failure", StringComparison.Ordinal))
            _userService.GetUserInfoAsync(requester.Id, Arg.Any<CancellationToken>())
                .Returns(ValueTask.FromException<UserInfo?>(new IOException("Language lookup unavailable")));

        switch (action)
        {
            case "approve":
                await _service.ApproveJoinRequestAsync(request.Id, coordinator.Id, null, TestContext.Current.CancellationToken);
                break;
            case "remove":
                await _service.RemoveMemberAsync(team.Id, requester.Id, coordinator.Id, TestContext.Current.CancellationToken);
                break;
            default:
                await _service.RejectJoinRequestAsync(request.Id, coordinator.Id, "Reason", TestContext.Current.CancellationToken);
                break;
        }

        var expectedSource = string.Equals(action, "remove", StringComparison.Ordinal)
            ? NotificationSource.TeamMemberRemoved : NotificationSource.TeamJoinRequestDecided;
        var args = Notifier.ReceivedCalls()
            .Where(call => call.GetArguments()[0] is NotificationSource source && source == expectedSource)
            .Single().GetArguments();
        ((IReadOnlyList<Guid>)args[4]!).Should().ContainSingle().Which.Should().Be(requester.Id);
        switch (action)
        {
            case "approve":
                args[3].Should().Be("Se ha aprobado tu solicitud para unirte a Alpha");
                args[5].Should().Be("¡Te damos la bienvenida a Alpha!");
                args[7].Should().Be("Ver equipo");
                break;
            case "remove":
                args[3].Should().Be("Se te ha eliminado de Alpha");
                break;
            case "reject":
                args[3].Should().Be("No se ha aprobado tu solicitud para unirte a Alpha");
                args[5].Should().Be("No se ha aprobado tu solicitud para unirte a Alpha.");
                args[7].Should().Be("Explorar equipos");
                break;
            default:
                args[3].Should().Be("Your request to join Alpha was not approved");
                break;
        }
    }

    [HumansFact]
    public async Task ApproveJoinRequestAsync_ApproverLacksPermission_Throws()
    {
        var stranger = SeedUser(displayName: "Stranger");
        var requester = SeedUser(displayName: "Requester");
        var team = SeedTeam("Alpha", requiresApproval: true);
        var request = SeedJoinRequest(team.Id, requester.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var act = () => _service.ApproveJoinRequestAsync(request.Id, stranger.Id, null, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*permission*");
        (await TeamsDb.TeamMembers.AnyAsync(tm => tm.UserId == requester.Id, TestContext.Current.CancellationToken))
            .Should().BeFalse();
    }

    [HumansFact]
    public async Task ApproveJoinRequestAsync_HappyPath_AddsMemberAndAuditsApproval()
    {
        var coordinator = SeedUser(displayName: "Coordinator");
        var requester = SeedUser(displayName: "Requester");
        var team = SeedTeam("Alpha", requiresApproval: true);
        SeedTeamMember(team.Id, coordinator.Id, TeamMemberRole.Coordinator);
        var request = SeedJoinRequest(team.Id, requester.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var member = await _service.ApproveJoinRequestAsync(request.Id, coordinator.Id, "welcome", TestContext.Current.CancellationToken);

        member.UserId.Should().Be(requester.Id);
        member.Role.Should().Be(TeamMemberRole.Member);

        ClearAllTrackers();
        var stored = await TeamsDb.TeamJoinRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id, TestContext.Current.CancellationToken);
        stored.Status.Should().Be(TeamJoinRequestStatus.Approved);
        stored.ReviewedByUserId.Should().Be(coordinator.Id);
        (await TeamsDb.TeamMembers.AsNoTracking().AnyAsync(
            tm => tm.TeamId == team.Id && tm.UserId == requester.Id && tm.LeftAt == null,
            TestContext.Current.CancellationToken)).Should().BeTrue();
        await AuditLog.Received(1).LogAsync(
            AuditAction.TeamJoinRequestApproved, nameof(Team), team.Id,
            Arg.Any<string>(), coordinator.Id,
            requester.Id, nameof(User));
    }

    // ==========================================================================
    // CreateRoleDefinitionAsync
    // ==========================================================================

    [HumansFact]
    public async Task CreateRoleDefinitionAsync_TeamNotFound_Throws()
    {
        var actor = SeedUser(displayName: "Actor");
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var act = () => _service.CreateRoleDefinitionAsync(
            Guid.NewGuid(), "Lead", null, 1, [SlotPriority.None], 0, RolePeriod.YearRound, actor.Id, cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Team*not found*");
    }

    [HumansFact]
    public async Task CreateRoleDefinitionAsync_DuplicateName_Throws()
    {
        var actor = SeedUser(displayName: "Actor");
        SeedRoleAssignment(actor.Id, RoleNames.Admin,
            Clock.GetCurrentInstant() - Duration.FromDays(1));
        var team = SeedTeam("Alpha");
        SeedTeamRoleDefinition(team.Id, isManagement: false);
        var existingName = "Member Role";
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var act = () => _service.CreateRoleDefinitionAsync(
            team.Id, existingName, null, 1, [SlotPriority.None], 0, RolePeriod.YearRound, actor.Id, cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
    }

    [HumansFact]
    public async Task CreateRoleDefinitionAsync_ActorLacksPermission_Throws()
    {
        var actor = SeedUser(displayName: "Actor");
        var team = SeedTeam("Alpha");
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var act = () => _service.CreateRoleDefinitionAsync(
            team.Id, "Lead", null, 1, [SlotPriority.None], 0, RolePeriod.YearRound, actor.Id, cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*permission*");
    }

    [HumansTheory]
    [InlineData(false, -1)]
    [InlineData(false, 99)]
    [InlineData(true, -1)]
    [InlineData(true, 99)]
    public async Task RoleDefinitionWrites_UndefinedPriority_RejectWithoutChangingDefinition(bool update, int priority)
    {
        var actor = SeedUser(displayName: "Actor");
        SeedRoleAssignment(actor.Id, RoleNames.Admin,
            Clock.GetCurrentInstant() - Duration.FromDays(1));
        var team = SeedTeam("Alpha");
        var definition = SeedTeamRoleDefinition(team.Id, isManagement: false);
        definition.Name = "Original";
        definition.Priorities = [SlotPriority.None];
        await SaveAllAsync(TestContext.Current.CancellationToken);

        Func<Task> act = update
            ? () => _service.UpdateRoleDefinitionAsync(
                definition.Id, "Changed", null, 1, [(SlotPriority)priority], 0, false,
                RolePeriod.Event, actor.Id, cancellationToken: TestContext.Current.CancellationToken)
            : () => _service.CreateRoleDefinitionAsync(
                team.Id, "Changed", null, 1, [(SlotPriority)priority], 0,
                RolePeriod.Event, actor.Id, cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*priority*");
        ClearAllTrackers();
        var stored = await TeamsDb.TeamRoleDefinitions.AsNoTracking()
            .SingleAsync(TestContext.Current.CancellationToken);
        stored.Name.Should().Be("Original");
        stored.Priorities.Should().Equal(SlotPriority.None);
        AuditLog.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task CreateRoleDefinitionAsync_HappyPath_PersistsDefinition()
    {
        var actor = SeedUser(displayName: "Actor");
        SeedRoleAssignment(actor.Id, RoleNames.Admin,
            Clock.GetCurrentInstant() - Duration.FromDays(1));
        var team = SeedTeam("Alpha");
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.CreateRoleDefinitionAsync(
            team.Id, "Lead", "Lead description", 2,
            [SlotPriority.Critical, SlotPriority.Important], 5, RolePeriod.Event, actor.Id,
            isPublic: false, cancellationToken: TestContext.Current.CancellationToken);

        result.TeamId.Should().Be(team.Id);
        result.Name.Should().Be("Lead");
        result.SlotCount.Should().Be(2);
        result.SortOrder.Should().Be(5);
        result.IsPublic.Should().BeFalse();
        result.Period.Should().Be(RolePeriod.Event);
        result.CreatedAt.Should().Be(Clock.GetCurrentInstant());
    }

    // ==========================================================================
    // UpdateRoleDefinitionAsync
    // ==========================================================================

    [HumansFact]
    public async Task UpdateRoleDefinitionAsync_DefinitionNotFound_Throws()
    {
        var actor = SeedUser(displayName: "Actor");
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var act = () => _service.UpdateRoleDefinitionAsync(
            Guid.NewGuid(), "Lead", null, 1, [SlotPriority.None], 0, false, RolePeriod.YearRound, actor.Id, cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Role definition*not found*");
    }

    [HumansFact]
    public async Task UpdateRoleDefinitionAsync_ActorLacksPermission_Throws()
    {
        var actor = SeedUser(displayName: "Actor");
        var team = SeedTeam("Alpha");
        var def = SeedTeamRoleDefinition(team.Id, isManagement: false);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var act = () => _service.UpdateRoleDefinitionAsync(
            def.Id, "Lead", null, 1, [SlotPriority.None], 0, false, RolePeriod.YearRound, actor.Id, cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*permission*");
    }

    [HumansFact]
    public async Task UpdateRoleDefinitionAsync_RenamingToExistingName_Throws()
    {
        var actor = SeedUser(displayName: "Actor");
        SeedRoleAssignment(actor.Id, RoleNames.Admin,
            Clock.GetCurrentInstant() - Duration.FromDays(1));
        var team = SeedTeam("Alpha");
        var first = SeedTeamRoleDefinition(team.Id, isManagement: false);
        first.Name = "Original";
        var second = SeedTeamRoleDefinition(team.Id, isManagement: false);
        second.Name = "Other";
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var act = () => _service.UpdateRoleDefinitionAsync(
            second.Id, "Original", null, 1, [SlotPriority.None], 0, false, RolePeriod.YearRound, actor.Id, cancellationToken: TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already exists*");
    }

    [HumansFact]
    public async Task UpdateRoleDefinitionAsync_CanToggleManagementFalse_PreservesIsManagement()
    {
        var actor = SeedUser(displayName: "Actor");
        SeedRoleAssignment(actor.Id, RoleNames.Admin,
            Clock.GetCurrentInstant() - Duration.FromDays(1));
        var team = SeedTeam("Alpha");
        var def = SeedTeamRoleDefinition(team.Id, isManagement: true);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        await _service.UpdateRoleDefinitionAsync(
            def.Id, def.Name, null, 1, [SlotPriority.None], 0,
            isManagement: false, RolePeriod.YearRound, actor.Id,
            canToggleManagement: false, cancellationToken: TestContext.Current.CancellationToken);

        ClearAllTrackers();
        var stored = await TeamsDb.TeamRoleDefinitions.AsNoTracking().SingleAsync(d => d.Id == def.Id, TestContext.Current.CancellationToken);
        stored.IsManagement.Should().BeTrue();
    }

    [HumansFact]
    public async Task UpdateRoleDefinitionAsync_HappyPath_UpdatesAllEditableFields()
    {
        var actor = SeedUser(displayName: "Actor");
        SeedRoleAssignment(actor.Id, RoleNames.Admin,
            Clock.GetCurrentInstant() - Duration.FromDays(1));
        var team = SeedTeam("Alpha");
        var def = SeedTeamRoleDefinition(team.Id, isManagement: false);
        def.Name = "Original";
        def.SortOrder = 0;
        await SaveAllAsync(TestContext.Current.CancellationToken);

        await _service.UpdateRoleDefinitionAsync(
            def.Id, "Renamed", "new desc", slotCount: 2,
            [SlotPriority.Critical, SlotPriority.None], sortOrder: 7,
            isManagement: false, RolePeriod.Event, actor.Id, isPublic: false, cancellationToken: TestContext.Current.CancellationToken);

        ClearAllTrackers();
        var stored = await TeamsDb.TeamRoleDefinitions.AsNoTracking().SingleAsync(d => d.Id == def.Id, TestContext.Current.CancellationToken);
        stored.Name.Should().Be("Renamed");
        stored.Description.Should().Be("new desc");
        stored.SlotCount.Should().Be(2);
        stored.SortOrder.Should().Be(7);
        stored.IsPublic.Should().BeFalse();
        stored.Period.Should().Be(RolePeriod.Event);
        stored.UpdatedAt.Should().Be(Clock.GetCurrentInstant());
    }

    // ==========================================================================
    // DeleteRoleDefinitionAsync
    // ==========================================================================

    [HumansFact]
    public async Task DeleteRoleDefinitionAsync_NotFound_Throws()
    {
        var actor = SeedUser();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var act = () => _service.DeleteRoleDefinitionAsync(Guid.NewGuid(), actor.Id, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Role definition*not found*");
    }

    [HumansFact]
    public async Task DeleteRoleDefinitionAsync_ActorLacksPermission_Throws()
    {
        var actor = SeedUser(displayName: "Actor");
        var team = SeedTeam("Alpha");
        var def = SeedTeamRoleDefinition(team.Id, isManagement: false);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var act = () => _service.DeleteRoleDefinitionAsync(def.Id, actor.Id, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*permission*");
    }

    [HumansFact]
    public async Task DeleteRoleDefinitionAsync_HappyPath_RemovesDefinition()
    {
        var actor = SeedUser(displayName: "Actor");
        SeedRoleAssignment(actor.Id, RoleNames.Admin,
            Clock.GetCurrentInstant() - Duration.FromDays(1));
        var team = SeedTeam("Alpha");
        var def = SeedTeamRoleDefinition(team.Id, isManagement: false);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        await _service.DeleteRoleDefinitionAsync(def.Id, actor.Id, TestContext.Current.CancellationToken);

        ClearAllTrackers();
        var stored = await TeamsDb.TeamRoleDefinitions.AsNoTracking().FirstOrDefaultAsync(d => d.Id == def.Id, TestContext.Current.CancellationToken);
        stored.Should().BeNull();
    }

    // ==========================================================================
    // ToggleRoleIsManagementAsync
    // ==========================================================================

    [HumansFact]
    public async Task ToggleRoleIsManagementAsync_DefinitionNotFound_Throws()
    {
        var actor = SeedUser();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var act = () => _service.ToggleRoleIsManagementAsync(Guid.NewGuid(), actor.Id, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*Role definition*not found*");
    }

    [HumansFact]
    public async Task ToggleRoleIsManagementAsync_ActorLacksPermission_Throws()
    {
        var actor = SeedUser(displayName: "Actor");
        var team = SeedTeam("Alpha");
        var def = SeedTeamRoleDefinition(team.Id, isManagement: false);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var act = () => _service.ToggleRoleIsManagementAsync(def.Id, actor.Id, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*permission*");
    }

    [HumansFact]
    public async Task ToggleRoleIsManagementAsync_PromotingWithAssignments_Throws()
    {
        var actor = SeedUser(displayName: "Actor");
        SeedRoleAssignment(actor.Id, RoleNames.Admin,
            Clock.GetCurrentInstant() - Duration.FromDays(1));
        var team = SeedTeam("Alpha");
        var def = SeedTeamRoleDefinition(team.Id, isManagement: false);
        var holder = SeedUser(displayName: "Holder");
        var member = SeedTeamMember(team.Id, holder.Id);
        SeedTeamRoleAssignment(def.Id, member.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var act = () => _service.ToggleRoleIsManagementAsync(def.Id, actor.Id, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*members are assigned*");
    }

    [HumansFact]
    public async Task ToggleRoleIsManagementAsync_PromotingWhenAnotherManagementExists_Throws()
    {
        var actor = SeedUser(displayName: "Actor");
        SeedRoleAssignment(actor.Id, RoleNames.Admin,
            Clock.GetCurrentInstant() - Duration.FromDays(1));
        var team = SeedTeam("Alpha");
        SeedTeamRoleDefinition(team.Id, isManagement: true);
        var def = SeedTeamRoleDefinition(team.Id, isManagement: false);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var act = () => _service.ToggleRoleIsManagementAsync(def.Id, actor.Id, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already marked as the management role*");
    }

    [HumansFact]
    public async Task ToggleRoleIsManagementAsync_HappyPathPromote_FlipsIsManagement()
    {
        var actor = SeedUser(displayName: "Actor");
        SeedRoleAssignment(actor.Id, RoleNames.Admin,
            Clock.GetCurrentInstant() - Duration.FromDays(1));
        var team = SeedTeam("Alpha");
        var def = SeedTeamRoleDefinition(team.Id, isManagement: false);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.ToggleRoleIsManagementAsync(def.Id, actor.Id, TestContext.Current.CancellationToken);

        result.IsManagement.Should().BeTrue();
        ClearAllTrackers();
        var stored = await TeamsDb.TeamRoleDefinitions.AsNoTracking().SingleAsync(d => d.Id == def.Id, TestContext.Current.CancellationToken);
        stored.IsManagement.Should().BeTrue();
    }

    [HumansFact]
    public async Task ToggleRoleIsManagementAsync_HappyPathDemote_FlipsIsManagement()
    {
        var actor = SeedUser(displayName: "Actor");
        SeedRoleAssignment(actor.Id, RoleNames.Admin,
            Clock.GetCurrentInstant() - Duration.FromDays(1));
        var team = SeedTeam("Alpha");
        var def = SeedTeamRoleDefinition(team.Id, isManagement: true);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.ToggleRoleIsManagementAsync(def.Id, actor.Id, TestContext.Current.CancellationToken);

        result.IsManagement.Should().BeFalse();
    }

    // ==========================================================================
    // PermanentlyDeleteTeamAsync
    // ==========================================================================

    [HumansFact]
    public async Task PermanentlyDeleteTeamAsync_TeamNotFound_Throws()
    {
        var act = () => _service.PermanentlyDeleteTeamAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not found*");
    }

    [HumansFact]
    public async Task PermanentlyDeleteTeamAsync_SystemTeam_Throws()
    {
        var team = SeedTeam("Volunteers", type: SystemTeamType.Volunteers);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var act = () => _service.PermanentlyDeleteTeamAsync(team.Id, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*system team*");
    }

    [HumansFact]
    public async Task PermanentlyDeleteTeamAsync_HasActiveChildren_Throws()
    {
        var parent = SeedTeam("Parent");
        var child = SeedTeam("Child");
        child.ParentTeamId = parent.Id;
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var act = () => _service.PermanentlyDeleteTeamAsync(parent.Id, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*sub-teams*");
    }

    // Happy-path PermanentlyDeleteTeamAsync needs a real transactional store; the in-memory
    // EF provider used in this harness does not support transactions. Cover it in Integration tests.

    // ==========================================================================
    // GetTeamBySlugAsync (TeamInfo? â€” ITeamService surface)
    // ==========================================================================

    [HumansFact]
    public async Task GetTeamBySlugAsync_ExistingSlug_ReturnsTeamWithActiveMembers()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id);
        SeedTeamMember(team.Id, SeedUser(displayName: "Left User").Id,
            leftAt: Clock.GetCurrentInstant() - Duration.FromDays(1));
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var info = await _service.GetTeamBySlugAsync("ALPHA", TestContext.Current.CancellationToken);

        info.Should().NotBeNull();
        info.Id.Should().Be(team.Id);
        info.Name.Should().Be("Alpha");
        info.Members.Should().ContainSingle().Which.UserId.Should().Be(user.Id);
    }

    [HumansFact]
    public async Task GetTeamBySlugAsync_NonExistentSlug_ReturnsNull()
    {
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetTeamBySlugAsync("non-existent", TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    // ==========================================================================
    // GetTeamAsync
    // ==========================================================================

    [HumansFact]
    public async Task GetTeamAsync_ExistingId_ReturnsTeamWithActiveMembers()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id);
        SeedTeamMember(team.Id, SeedUser(displayName: "Left User").Id,
            leftAt: Clock.GetCurrentInstant() - Duration.FromDays(1));
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetTeamAsync(team.Id, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.Members.Should().ContainSingle();
    }

    [HumansFact]
    public async Task GetTeamAsync_NonExistentId_ReturnsNull()
    {
        var result = await _service.GetTeamAsync(Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    // ==========================================================================
    // GetUserTeamsAsync
    // ==========================================================================

    [HumansFact]
    public async Task GetUserTeamsAsync_ReturnsActiveMembershipsOnly()
    {
        var user = SeedUser();
        var teamA = SeedTeam("Alpha");
        var teamB = SeedTeam("Beta");
        SeedTeamMember(teamA.Id, user.Id);
        SeedTeamMember(teamB.Id, user.Id,
            leftAt: Clock.GetCurrentInstant() - Duration.FromDays(1));
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetUserTeamMembershipsAsync(user.Id, TestContext.Current.CancellationToken);

        result.Should().ContainSingle();
        result[0].TeamId.Should().Be(teamA.Id);
    }

    [HumansFact]
    public async Task GetUserTeamsAsync_IncludesTeamNavigation()
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, user.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetUserTeamMembershipsAsync(user.Id, TestContext.Current.CancellationToken);

        result.Single().TeamName.Should().Be("Alpha");
    }

    [HumansFact]
    public async Task GetUserTeamsAsync_NoMemberships_ReturnsEmpty()
    {
        var user = SeedUser();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetUserTeamMembershipsAsync(user.Id, TestContext.Current.CancellationToken);

        result.Should().BeEmpty();
    }

    // ==========================================================================
    // GetMyTeamMembershipsAsync
    // ==========================================================================

    [HumansFact]
    public async Task GetMyTeamMembershipsAsync_Coordinator_GetsPendingCountsForManageableNonSystemTeams()
    {
        var user = SeedUser(displayName: "Coordinator");
        var managedTeam = SeedTeam("Alpha", requiresApproval: true);
        var systemTeam = SeedTeam("Volunteers", type: SystemTeamType.Volunteers, requiresApproval: true);
        SeedTeamMember(managedTeam.Id, user.Id, TeamMemberRole.Coordinator);
        SeedTeamMember(systemTeam.Id, user.Id, TeamMemberRole.Coordinator);
        SeedJoinRequest(managedTeam.Id, SeedUser(displayName: "Requester A").Id);
        SeedJoinRequest(systemTeam.Id, SeedUser(displayName: "Requester B").Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _pages.GetMyTeamMembershipsAsync(user.Id, TestContext.Current.CancellationToken);

        result.Should().HaveCount(2);
        result.Single(m => m.TeamId == managedTeam.Id).PendingRequestCount.Should().Be(1);
        result.Single(m => m.TeamId == managedTeam.Id).CanLeave.Should().BeTrue();
        result.Single(m => m.TeamId == systemTeam.Id).PendingRequestCount.Should().Be(0);
        result.Single(m => m.TeamId == systemTeam.Id).CanLeave.Should().BeFalse();
    }

    [HumansFact]
    public async Task GetMyTeamMembershipsAsync_BoardMember_GetsPendingCountsForRegularMemberships()
    {
        var user = SeedUser(displayName: "Board Human");
        SeedRoleAssignment(
            user.Id,
            RoleNames.Board,
            Clock.GetCurrentInstant() - Duration.FromDays(10));
        var team = SeedTeam("Alpha", requiresApproval: true);
        SeedTeamMember(team.Id, user.Id);
        SeedJoinRequest(team.Id, SeedUser(displayName: "Requester").Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _pages.GetMyTeamMembershipsAsync(user.Id, TestContext.Current.CancellationToken);

        result.Should().ContainSingle();
        result[0].Role.Should().Be(TeamMemberRole.Member);
        result[0].PendingRequestCount.Should().Be(1);
    }

    // ==========================================================================
    // GetTeamDetailAsync
    // ==========================================================================

    [HumansFact]
    public async Task GetTeamDetailAsync_AnonymousViewer_OnlySeesPublicTeamCoordinatorsAndPublicChildren()
    {
        var coordinator = SeedUser(displayName: "Coordinator");
        var member = SeedUser(displayName: "Member");
        var team = SeedTeam("Alpha");
        team.IsPublicPage = true;
        SeedTeamMember(team.Id, coordinator.Id, TeamMemberRole.Coordinator);
        SeedTeamMember(team.Id, member.Id);

        var publicChild = SeedTeam("Public Child");
        publicChild.ParentTeamId = team.Id;
        publicChild.IsPublicPage = true;

        var privateChild = SeedTeam("Private Child");
        privateChild.ParentTeamId = team.Id;
        privateChild.IsPublicPage = false;

        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _pages.GetTeamDetailAsync(team.Slug, userId: null, cancellationToken: TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.IsAuthenticated.Should().BeFalse();
        result.Members.Select(m => m.DisplayName).Should().BeEquivalentTo(
            ["Coordinator"],
            cfg => cfg.WithStrictOrdering());
        result.ChildTeams.Select(t => t.Name).Should().BeEquivalentTo(
            ["Public Child"],
            cfg => cfg.WithStrictOrdering());
        result.CanCurrentUserManage.Should().BeFalse();
        result.RoleDefinitions.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetTeamDetailAsync_AuthenticatedCoordinator_ReturnsViewerStateMembersAndPendingCount()
    {
        var coordinator = SeedUser(displayName: "Coordinator");
        var member = SeedUser(displayName: "Member");
        var requester = SeedUser(displayName: "Requester");
        var team = SeedTeam("Alpha", requiresApproval: true);
        SeedTeamMember(team.Id, coordinator.Id, TeamMemberRole.Coordinator);
        SeedTeamMember(team.Id, member.Id);
        var pendingRequest = SeedJoinRequest(team.Id, requester.Id);
        var roleDefinition = SeedTeamRoleDefinition(team.Id, isManagement: true);

        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _pages.GetTeamDetailAsync(team.Slug, coordinator.Id, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.IsAuthenticated.Should().BeTrue();
        result.IsCurrentUserMember.Should().BeTrue();
        result.IsCurrentUserCoordinator.Should().BeTrue();
        result.CanCurrentUserManage.Should().BeTrue();
        result.CanCurrentUserEditTeam.Should().BeFalse();
        result.CanCurrentUserJoin.Should().BeFalse();
        result.CanCurrentUserLeave.Should().BeTrue();
        result.PendingRequestCount.Should().Be(1);
        result.CurrentUserPendingRequestId.Should().BeNull();
        result.Members.Select(m => (m.DisplayName, m.Role)).Should().BeEquivalentTo(
            [("Member", TeamMemberRole.Member), ("Coordinator", TeamMemberRole.Coordinator)],
            cfg => cfg.WithStrictOrdering());
        result.RoleDefinitions.Select(d => d.Id).Should().ContainSingle().Which.Should().Be(roleDefinition.Id);
        pendingRequest.Status.Should().Be(TeamJoinRequestStatus.Pending);
    }

    // ==========================================================================
    // GetPendingRequestsForTeamAsync
    // ==========================================================================

    [HumansFact]
    public async Task GetPendingRequestsForTeamAsync_ReturnsPendingOnly()
    {
        var team = SeedTeam("Alpha");
        var u1 = SeedUser(displayName: "U1");
        var u2 = SeedUser(displayName: "U2");
        SeedJoinRequest(team.Id, u1.Id);
        SeedJoinRequest(team.Id, u2.Id, TeamJoinRequestStatus.Rejected);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetPendingRequestsForTeamAsync(team.Id, TestContext.Current.CancellationToken);

        result.Should().ContainSingle();
        result[0].UserId.Should().Be(u1.Id);
    }

    [HumansFact]
    public async Task GetPendingRequestsForTeamAsync_NoRequests_ReturnsEmpty()
    {
        var team = SeedTeam("Alpha");
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetPendingRequestsForTeamAsync(team.Id, TestContext.Current.CancellationToken);

        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetPendingRequestsForTeamAsync_OrderedByRequestedAt()
    {
        var team = SeedTeam("Alpha");
        var u1 = SeedUser(displayName: "First");
        var u2 = SeedUser(displayName: "Second");
        // Seed first request earlier
        var earlier = new TeamJoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            UserId = u1.Id,
            Status = TeamJoinRequestStatus.Pending,
            RequestedAt = Clock.GetCurrentInstant() - Duration.FromHours(2)
        };
        TeamsDb.TeamJoinRequests.Add(earlier);
        var later = new TeamJoinRequest
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            UserId = u2.Id,
            Status = TeamJoinRequestStatus.Pending,
            RequestedAt = Clock.GetCurrentInstant() - Duration.FromHours(1)
        };
        TeamsDb.TeamJoinRequests.Add(later);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetPendingRequestsForTeamAsync(team.Id, TestContext.Current.CancellationToken);

        result[0].UserId.Should().Be(u1.Id);
        result[1].UserId.Should().Be(u2.Id);
    }

    // ==========================================================================
    // GetUserPendingRequestAsync
    // ==========================================================================

    [HumansTheory]
    [InlineData("missing")]
    [InlineData("system")]
    [InlineData("nonmember")]
    public async Task LeaveTeamAsync_ExpectedRefusal_DoesNotThrowOrRemoveMembership(string refusal)
    {
        var user = SeedUser();
        var team = SeedTeam("Alpha", type: string.Equals(refusal, "system", StringComparison.Ordinal) ? SystemTeamType.Volunteers : SystemTeamType.None);
        var member = SeedTeamMember(team.Id, user.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.LeaveTeamAsync(
            string.Equals(refusal, "missing", StringComparison.Ordinal) ? Guid.NewGuid() : team.Id,
            string.Equals(refusal, "nonmember", StringComparison.Ordinal) ? Guid.NewGuid() : user.Id,
            TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.ErrorKey.Should().Be(refusal switch
        {
            "missing" => "Teams_NotFound",
            "system" => "Teams_CannotLeaveSystem",
            _ => "Teams_NotMember"
        });
        TeamsDb.ChangeTracker.Clear();
        (await TeamsDb.TeamMembers.SingleAsync(m => m.Id == member.Id, TestContext.Current.CancellationToken))
            .LeftAt.Should().BeNull();
    }

    [HumansFact]
    public async Task WithdrawJoinRequestAsync_OtherMembersRequest_DoesNotThrowOrWithdraw()
    {
        var team = SeedTeam("Alpha");
        var owner = SeedUser();
        var other = SeedUser();
        var request = SeedJoinRequest(team.Id, owner.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.WithdrawJoinRequestAsync(request.Id, other.Id, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeFalse();
        result.ErrorKey.Should().Be("Teams_RequestUnavailable");
        TeamsDb.ChangeTracker.Clear();
        (await TeamsDb.TeamJoinRequests.SingleAsync(r => r.Id == request.Id, TestContext.Current.CancellationToken))
            .Status.Should().Be(TeamJoinRequestStatus.Pending);
    }

    [HumansFact]
    public async Task GetPendingRequestsForUserAsync_ExcludesOtherMembersAndResolvedRequests()
    {
        var team = SeedTeam("Alpha");
        var user = SeedUser();
        var other = SeedUser();
        var pending = SeedJoinRequest(team.Id, user.Id);
        SeedJoinRequest(team.Id, other.Id);
        SeedJoinRequest(team.Id, user.Id, TeamJoinRequestStatus.Approved);
        SeedJoinRequest(team.Id, user.Id, TeamJoinRequestStatus.Rejected);
        SeedJoinRequest(team.Id, user.Id, TeamJoinRequestStatus.Withdrawn);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetPendingRequestsForUserAsync(user.Id, TestContext.Current.CancellationToken);

        var request = result.Should().ContainSingle().Subject;
        request.Id.Should().Be(pending.Id);
        request.UserId.Should().Be(user.Id);
        request.TeamName.Should().Be(team.Name);
        request.TeamSlug.Should().Be(team.Slug);
        await _service.WithdrawJoinRequestAsync(pending.Id, user.Id, TestContext.Current.CancellationToken);
        (await _service.GetPendingRequestsForUserAsync(user.Id, TestContext.Current.CancellationToken))
            .Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetUserPendingRequestAsync_HasPending_ReturnsRequest()
    {
        var team = SeedTeam("Alpha");
        var user = SeedUser();
        SeedJoinRequest(team.Id, user.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetUserPendingRequestAsync(team.Id, user.Id, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.UserId.Should().Be(user.Id);
    }

    [HumansFact]
    public async Task GetUserPendingRequestAsync_NoRequest_ReturnsNull()
    {
        var team = SeedTeam("Alpha");
        var user = SeedUser();
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetUserPendingRequestAsync(team.Id, user.Id, TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    [HumansFact]
    public async Task GetUserPendingRequestAsync_ApprovedRequest_ReturnsNull()
    {
        var team = SeedTeam("Alpha");
        var user = SeedUser();
        SeedJoinRequest(team.Id, user.Id, TeamJoinRequestStatus.Approved);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetUserPendingRequestAsync(team.Id, user.Id, TestContext.Current.CancellationToken);

        result.Should().BeNull();
    }

    // ==========================================================================
    // GetTeamAsync
    // ==========================================================================

    [HumansFact]
    public async Task GetTeamAsync_ReturnsOnlyActiveMembers()
    {
        var team = SeedTeam("Alpha");
        var active = SeedUser(displayName: "Active");
        var left = SeedUser(displayName: "Left");
        SeedTeamMember(team.Id, active.Id);
        SeedTeamMember(team.Id, left.Id,
            leftAt: Clock.GetCurrentInstant() - Duration.FromDays(1));
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetTeamAsync(team.Id, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.Members.Should().ContainSingle();
        result.Members[0].UserId.Should().Be(active.Id);
    }

    [HumansFact]
    public async Task GetTeamAsync_IncludesMemberRoleAndJoinedAt()
    {
        var team = SeedTeam("Alpha");
        var coordinator = SeedUser(displayName: "Coordinator");
        var memberEarly = SeedUser(displayName: "Early");
        var memberLate = SeedUser(displayName: "Late");
        // The active team read model preserves membership facts; presentation owns display ordering.
        var m1 = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            UserId = coordinator.Id,
            Role = TeamMemberRole.Coordinator,
            JoinedAt = Clock.GetCurrentInstant() - Duration.FromDays(5)
        };
        var m2 = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            UserId = memberEarly.Id,
            Role = TeamMemberRole.Member,
            JoinedAt = Clock.GetCurrentInstant() - Duration.FromDays(3)
        };
        var m3 = new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            UserId = memberLate.Id,
            Role = TeamMemberRole.Member,
            JoinedAt = Clock.GetCurrentInstant() - Duration.FromDays(1)
        };
        await TeamsDb.TeamMembers.AddRangeAsync(m1, m2, m3);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetTeamAsync(team.Id, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.Members.Should().HaveCount(3);
        result.Members.Count(m => m.Role == TeamMemberRole.Member).Should().Be(2);
        result.Members.Count(m => m.Role == TeamMemberRole.Coordinator).Should().Be(1);
        result.Members.Select(m => m.UserId).Should().Contain([memberEarly.Id, memberLate.Id]);
    }

    [HumansFact]
    public async Task GetTeamAsync_IncludesUserSlice()
    {
        var team = SeedTeam("Alpha");
        var user = SeedUser(displayName: "Alice");
        SeedTeamMember(team.Id, user.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetTeamAsync(team.Id, TestContext.Current.CancellationToken);

        var member = result!.Members.Should().ContainSingle().Subject;
        member.DisplayName.Should().Be("Alice");
    }

    [HumansFact]
    public async Task GetTeamAsync_NoMembers_ReturnsEmpty()
    {
        var team = SeedTeam("Alpha");
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetTeamAsync(team.Id, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.Members.Should().BeEmpty();
    }

    // ==========================================================================
    // GetAdminTeamListAsync
    // ==========================================================================

    [HumansFact]
    public async Task GetAdminTeamListAsync_ReturnsPaginatedResults()
    {
        SeedTeam("Alpha");
        SeedTeam("Beta");
        SeedTeam("Charlie");
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetAdminTeamListAsync(1, 2, TestContext.Current.CancellationToken);

        result.Teams.Should().HaveCount(2);
        result.TotalCount.Should().Be(3);
    }

    [HumansFact]
    public async Task GetAdminTeamListAsync_SecondPage_ReturnsRemainingItems()
    {
        SeedTeam("Alpha");
        SeedTeam("Beta");
        SeedTeam("Charlie");
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetAdminTeamListAsync(2, 2, TestContext.Current.CancellationToken);

        result.Teams.Should().ContainSingle();
        result.TotalCount.Should().Be(3);
    }

    [HumansFact]
    public async Task GetAdminTeamListAsync_IncludesMemberCount()
    {
        var team = SeedTeam("Alpha");
        var active = SeedUser(displayName: "Active");
        SeedTeamMember(team.Id, active.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetAdminTeamListAsync(1, 10, TestContext.Current.CancellationToken);

        result.Teams.Single().MemberCount.Should().Be(1);
    }

    [HumansFact]
    public async Task GetAdminTeamListAsync_IncludesPendingRequestCount()
    {
        var team = SeedTeam("Alpha");
        var u1 = SeedUser(displayName: "U1");
        SeedJoinRequest(team.Id, u1.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetAdminTeamListAsync(1, 10, TestContext.Current.CancellationToken);

        result.Teams.Single().PendingRequestCount.Should().Be(1);
    }

    [HumansFact]
    public async Task GetAdminTeamListAsync_IncludesInactiveTeams()
    {
        SeedTeam("Active");
        SeedTeam("Inactive", isActive: false);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetAdminTeamListAsync(1, 10, TestContext.Current.CancellationToken);

        result.TotalCount.Should().Be(2);
        result.Teams.Should().HaveCount(2);
    }

    [HumansFact]
    public async Task GetAdminTeamListAsync_SystemTeamsOrderedFirst()
    {
        SeedTeam("Zebra");
        SeedTeam("Volunteers", type: SystemTeamType.Volunteers);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetAdminTeamListAsync(1, 10, TestContext.Current.CancellationToken);

        // SystemTeamType.None(0) < Volunteers(1), so None sorts first in ascending order
        result.Teams[0].SystemTeamType.Should().BeNull();
        result.Teams[1].SystemTeamType.Should().Be(nameof(SystemTeamType.Volunteers));
    }

    // ==========================================================================
    // AddMemberToTeamAsync
    // ==========================================================================

    [HumansTheory]
    [InlineData(false, "missing")]
    [InlineData(true, "missing")]
    [InlineData(false, "deleted")]
    [InlineData(true, "deleted")]
    [InlineData(false, "merged")]
    [InlineData(true, "merged")]
    public async Task MembershipGrant_InvalidPerson_DoesNotCreateFootprint(bool assignRole, string kind)
    {
        var actor = SeedUser(displayName: "Actor");
        SeedRoleAssignment(actor.Id, RoleNames.Admin, Clock.GetCurrentInstant() - Duration.FromDays(1));
        var team = SeedTeam("Alpha");
        var definition = SeedTeamRoleDefinition(team.Id, isManagement: true);
        var targetId = Guid.NewGuid();
        if (!string.Equals(kind, "missing", StringComparison.Ordinal))
        {
            var target = SeedUser(displayName: "Terminal");
            targetId = target.Id;
            target.State = string.Equals(kind, "deleted", StringComparison.Ordinal) ? UserState.Deleted : UserState.Merged;
            target.MergedAt = Clock.GetCurrentInstant();
            target.MergedToUserId = string.Equals(kind, "merged", StringComparison.Ordinal) ? Guid.NewGuid() : null;
        }
        await SaveAllAsync(TestContext.Current.CancellationToken);

        Func<Task> add = async () =>
        {
            if (assignRole)
                await _service.AssignToRoleAsync(definition.Id, targetId, actor.Id, TestContext.Current.CancellationToken);
            else
                await _service.AddMemberToTeamAsync(team.Id, targetId, actor.Id, TestContext.Current.CancellationToken);
        };

        await add.Should().ThrowAsync<InvalidOperationException>();
        (await TeamsDb.TeamMembers.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await GoogleIntegrationDb.GoogleSyncOutboxEvents.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        (await TeamsDb.TeamRoleAssignments.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        AuditLog.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddMemberToTeamAsync_MergeAlias_UsesSurvivorForMembershipAndPendingRequest(bool alreadyMember)
    {
        var actor = SeedUser(displayName: "Actor");
        var target = SeedUser(displayName: "Survivor");
        var team = SeedTeam("Alpha");
        var sourceId = Guid.NewGuid();
        var request = SeedJoinRequest(team.Id, target.Id);
        if (alreadyMember) SeedTeamMember(team.Id, target.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);
        _userService.GetUserInfoAsync(sourceId, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(
            UserInfo.Create(target, [], [], [], null, [])));

        if (alreadyMember)
        {
            var add = () => _service.AddMemberToTeamAsync(team.Id, sourceId, actor.Id, TestContext.Current.CancellationToken);
            await add.Should().ThrowAsync<InvalidOperationException>().WithMessage("*already a member*");
            AuditLog.ReceivedCalls().Should().BeEmpty();
        }
        else
            (await _service.AddMemberToTeamAsync(team.Id, sourceId, actor.Id, TestContext.Current.CancellationToken))
                .UserId.Should().Be(target.Id);

        TeamsDb.ChangeTracker.Clear();
        var member = await TeamsDb.TeamMembers.AsNoTracking().SingleAsync(TestContext.Current.CancellationToken);
        member.UserId.Should().Be(target.Id);
        var savedRequest = await TeamsDb.TeamJoinRequests.AsNoTracking().SingleAsync(r => r.Id == request.Id,
            TestContext.Current.CancellationToken);
        savedRequest.Status.Should().Be(alreadyMember ? TeamJoinRequestStatus.Pending : TeamJoinRequestStatus.Approved);
        var outbox = await GoogleIntegrationDb.GoogleSyncOutboxEvents.AsNoTracking().ToListAsync(TestContext.Current.CancellationToken);
        if (alreadyMember) outbox.Should().BeEmpty();
        else outbox.Should().ContainSingle().Which.UserId.Should().Be(target.Id);
    }

    [HumansFact]
    public async Task AssignToRoleAsync_MergeAlias_UsesExistingSurvivorMembership()
    {
        var actor = SeedUser(displayName: "Actor");
        SeedRoleAssignment(actor.Id, RoleNames.Admin, Clock.GetCurrentInstant() - Duration.FromDays(1));
        var target = SeedUser(displayName: "Survivor");
        var team = SeedTeam("Alpha");
        var member = SeedTeamMember(team.Id, target.Id);
        var definition = SeedTeamRoleDefinition(team.Id, isManagement: false);
        await SaveAllAsync(TestContext.Current.CancellationToken);
        var sourceId = Guid.NewGuid();
        _userService.GetUserInfoAsync(sourceId, Arg.Any<CancellationToken>()).Returns(new ValueTask<UserInfo?>(
            UserInfo.Create(target, [], [], [], null, [])));

        var assignment = await _service.AssignToRoleAsync(definition.Id, sourceId, actor.Id,
            TestContext.Current.CancellationToken);

        assignment.TeamMemberId.Should().Be(member.Id);
        (await TeamsDb.TeamMembers.CountAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        (await GoogleIntegrationDb.GoogleSyncOutboxEvents.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
    }

    [HumansFact]
    public async Task AddMemberToTeamAsync_ValidUser_CreatesMembership()
    {
        var actor = SeedUser(displayName: "Actor");
        var target = SeedUser(displayName: "Target");
        var team = SeedTeam("Alpha");
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.AddMemberToTeamAsync(team.Id, target.Id, actor.Id, TestContext.Current.CancellationToken);

        result.Should().NotBeNull();
        result.TeamId.Should().Be(team.Id);
        result.UserId.Should().Be(target.Id);
        result.Role.Should().Be(TeamMemberRole.Member);
        result.LeftAt.Should().BeNull();

        var memberInDb = await TeamsDb.TeamMembers
            .FirstOrDefaultAsync(tm => tm.TeamId == team.Id && tm.UserId == target.Id && tm.LeftAt == null, TestContext.Current.CancellationToken);
        memberInDb.Should().NotBeNull();
    }

    [HumansTheory]
    [InlineData(GoogleEmailStatus.Valid, 1)]
    [InlineData(GoogleEmailStatus.Rejected, 0)]
    public async Task AddMemberToTeamAsync_RejectedGoogleIdentity_SkipsTheOutboxEvent(GoogleEmailStatus status, int events)
    {
        var actor = SeedUser(displayName: "Actor");
        var target = SeedUser(displayName: "Target");
        var team = SeedTeam("Alpha");
        await SaveAllAsync(TestContext.Current.CancellationToken);
        _googleIdentities.GetWorkspaceIdentityAsync(target.Id, Arg.Any<CancellationToken>())
            .Returns(new WorkspaceIdentity(target.Id, "target@example.org", status));

        await _service.AddMemberToTeamAsync(team.Id, target.Id, actor.Id, TestContext.Current.CancellationToken);

        (await GoogleIntegrationDb.GoogleSyncOutboxEvents.CountAsync(TestContext.Current.CancellationToken))
            .Should().Be(events);
    }

    [HumansFact]
    public async Task AddMemberToTeamAsync_PendingRequest_ApprovesWithMembership()
    {
        var actor = SeedUser(displayName: "Actor");
        var target = SeedUser(displayName: "Target");
        var team = SeedTeam("Alpha");
        var request = SeedJoinRequest(team.Id, target.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        await _service.AddMemberToTeamAsync(team.Id, target.Id, actor.Id, TestContext.Current.CancellationToken);

        TeamsDb.ChangeTracker.Clear();
        var saved = await TeamsDb.TeamJoinRequests.SingleAsync(r => r.Id == request.Id, TestContext.Current.CancellationToken);
        saved.Status.Should().Be(TeamJoinRequestStatus.Approved);
        saved.ReviewedByUserId.Should().Be(actor.Id);
        saved.ReviewNotes.Should().Be("Added directly by team manager");
        saved.ResolvedAt.Should().Be(Clock.GetCurrentInstant());
        (await TeamsDb.TeamMembers.CountAsync(m => m.TeamId == team.Id && m.UserId == target.Id && m.LeftAt == null,
            TestContext.Current.CancellationToken)).Should().Be(1);
    }

    [HumansFact]
    public async Task AddMemberToTeamAsync_AuditsTheAdd()
    {
        var actor = SeedUser(displayName: "Actor");
        var target = SeedUser(displayName: "Target");
        var team = SeedTeam("Alpha");
        await SaveAllAsync(TestContext.Current.CancellationToken);

        await _service.AddMemberToTeamAsync(team.Id, target.Id, actor.Id, TestContext.Current.CancellationToken);

        await AuditLog.Received(1).LogAsync(
            AuditAction.TeamMemberAdded, nameof(Team), team.Id,
            Arg.Any<string>(), actor.Id,
            target.Id, nameof(User));
    }

    [HumansFact]
    public async Task RemoveMemberAsync_AuditsTheRemoval()
    {
        var actor = SeedUser(displayName: "Actor");
        var target = SeedUser(displayName: "Target");
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, actor.Id, TeamMemberRole.Coordinator);
        SeedTeamMember(team.Id, target.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        await _service.RemoveMemberAsync(team.Id, target.Id, actor.Id, TestContext.Current.CancellationToken);

        await AuditLog.Received(1).LogAsync(
            AuditAction.TeamMemberRemoved, nameof(Team), team.Id,
            Arg.Any<string>(), actor.Id,
            target.Id, nameof(User));
    }

    [HumansFact]
    public async Task AddMemberToTeamAsync_AlreadyMember_Throws()
    {
        var actor = SeedUser(displayName: "Actor");
        var target = SeedUser(displayName: "Target");
        var team = SeedTeam("Alpha");
        SeedTeamMember(team.Id, target.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var act = () => _service.AddMemberToTeamAsync(team.Id, target.Id, actor.Id, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*already a member*");
    }

    [HumansFact]
    public async Task AddMemberToTeamAsync_SystemTeam_Throws()
    {
        var actor = SeedUser(displayName: "Actor");
        var target = SeedUser(displayName: "Target");
        var team = SeedTeam("Volunteers", type: SystemTeamType.Volunteers);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var act = () => _service.AddMemberToTeamAsync(team.Id, target.Id, actor.Id, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*system team*");
    }

    [HumansFact]
    public async Task LeaveTeamAsync_RemovesManagementAssignments_InvalidatesShiftAuthorizationCache()
    {
        var user = SeedUser();
        var team = SeedTeam("Operations");
        var roleDefinition = SeedTeamRoleDefinition(team.Id, isManagement: true);
        var member = SeedTeamMember(team.Id, user.Id, TeamMemberRole.Coordinator);
        SeedTeamRoleAssignment(roleDefinition.Id, member.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);
        Cache.Set(CacheKeys.ShiftAuthorization(user.Id), new[] { team.Id });

        var result = await _service.LeaveTeamAsync(team.Id, user.Id, TestContext.Current.CancellationToken);

        result.Succeeded.Should().BeTrue();
        result.WasCoordinator.Should().BeTrue();
        Cache.TryGetValue(CacheKeys.ShiftAuthorization(user.Id), out _).Should().BeFalse();
    }

    [HumansFact]
    public async Task GetRosterAsync_ExpandsSlotsWithPrioritiesAndAssignments()
    {
        var alphaTeam = SeedTeam("Alpha");
        var betaTeam = SeedTeam("Beta");
        var alphaMember = SeedUser(displayName: "Assigned Human");
        var alphaTeamMember = SeedTeamMember(alphaTeam.Id, alphaMember.Id);

        var alphaDefinition = new TeamRoleDefinition
        {
            Id = Guid.NewGuid(),
            TeamId = alphaTeam.Id,
            Team = alphaTeam,
            Name = "Lead",
            IsPublic = false,
            SlotCount = 2,
            Priorities = [SlotPriority.Important, SlotPriority.None],
            SortOrder = 0,
            Period = RolePeriod.YearRound,
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        };
        alphaDefinition.Assignments.Add(new TeamRoleAssignment
        {
            Id = Guid.NewGuid(),
            TeamRoleDefinitionId = alphaDefinition.Id,
            TeamMemberId = alphaTeamMember.Id,
            TeamMember = alphaTeamMember,
            SlotIndex = 0,
            AssignedAt = Clock.GetCurrentInstant(),
            AssignedByUserId = Guid.NewGuid()
        });

        var betaDefinition = new TeamRoleDefinition
        {
            Id = Guid.NewGuid(),
            TeamId = betaTeam.Id,
            Team = betaTeam,
            Name = "Greeter",
            SlotCount = 1,
            Priorities = [SlotPriority.Critical],
            SortOrder = 0,
            Period = RolePeriod.Event,
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        };

        var inactiveTeam = SeedTeam("Inactive");
        inactiveTeam.IsActive = false;
        var systemTeam = SeedTeam("System");
        systemTeam.SystemTeamType = SystemTeamType.Volunteers;
        await TeamsDb.TeamRoleDefinitions.AddRangeAsync(alphaDefinition, betaDefinition,
            new TeamRoleDefinition { Id = Guid.NewGuid(), TeamId = inactiveTeam.Id, Name = "Excluded inactive" },
            new TeamRoleDefinition { Id = Guid.NewGuid(), TeamId = systemTeam.Id, Name = "Excluded system" });
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _pages.GetRosterAsync(priority: null, status: null, period: null, cancellationToken: TestContext.Current.CancellationToken);

        result.Select(slot => (slot.TeamName, slot.RoleName, slot.SlotNumber))
            .Should().BeEquivalentTo(new[]
            {
                ("Beta", "Greeter", 1),
                ("Alpha", "Lead", 1),
                ("Alpha", "Lead", 2)
            });

        var critical = result.Single(slot => string.Equals(slot.TeamName, "Beta", StringComparison.Ordinal));
        critical.IsPublic.Should().BeTrue();
        result.Where(slot => slot.RoleDefinitionId == alphaDefinition.Id).Should().OnlyContain(slot => !slot.IsPublic);
        critical.Priority.Should().Be(nameof(SlotPriority.Critical));
        critical.PriorityBadgeClass.Should().Be("bg-danger");
        result.Single(slot => string.Equals(slot.TeamName, "Alpha", StringComparison.Ordinal) && slot.SlotNumber == 1)
            .AssignedUserName.Should().Be("Assigned Human");
        var unprioritized = result.Single(slot => string.Equals(slot.TeamName, "Alpha", StringComparison.Ordinal) && slot.SlotNumber == 2);
        unprioritized.Priority.Should().Be(nameof(SlotPriority.None));
        unprioritized.PriorityBadgeClass.Should().Be("bg-light text-dark");
    }

    [HumansFact]
    public async Task GetRosterAsync_AppliesPriorityStatusAndPeriodFilters()
    {
        var team = SeedTeam("Alpha");
        var assignedUser = SeedUser(displayName: "Assigned Human");
        var assignedTeamMember = SeedTeamMember(team.Id, assignedUser.Id);

        var definition = new TeamRoleDefinition
        {
            Id = Guid.NewGuid(),
            TeamId = team.Id,
            Team = team,
            Name = "Lead",
            SlotCount = 3,
            Priorities = [SlotPriority.Critical, SlotPriority.Important, SlotPriority.Important],
            SortOrder = 0,
            Period = RolePeriod.Event,
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        };
        definition.Assignments.Add(new TeamRoleAssignment
        {
            Id = Guid.NewGuid(),
            TeamRoleDefinitionId = definition.Id,
            TeamMemberId = assignedTeamMember.Id,
            TeamMember = assignedTeamMember,
            SlotIndex = 1,
            AssignedAt = Clock.GetCurrentInstant(),
            AssignedByUserId = Guid.NewGuid()
        });

        TeamsDb.TeamRoleDefinitions.Add(definition);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _pages.GetRosterAsync(
            priority: nameof(SlotPriority.Important),
            status: "open",
            period: nameof(RolePeriod.Event), cancellationToken: TestContext.Current.CancellationToken);

        result.Should().ContainSingle();
        result[0].SlotNumber.Should().Be(3);
        result[0].Priority.Should().Be(nameof(SlotPriority.Important));
        result[0].Period.Should().Be(nameof(RolePeriod.Event));
        result[0].IsFilled.Should().BeFalse();
    }

    // ==========================================================================
    // GetAdminTeamListAsync â€” PendingShiftSignupCount
    // ==========================================================================

    [HumansFact]
    public async Task GetAdminTeamListAsync_CountsPendingShiftSignupsForActiveEvent()
    {
        var team = SeedTeam("Dept A");
        var user = SeedUser();
        SeedTeamMember(team.Id, user.Id);

        var activeEvent = SeedEventSettings("Active Event", isActive: true);
        var inactiveEvent = SeedEventSettings("Old Event", isActive: false);

        // Rota on active event with 2 pending signups
        var activeRota = SeedRota(team.Id, activeEvent.Id, "Gate Shifts");
        var shift1 = SeedShift(activeRota.Id);
        SeedShiftSignup(shift1.Id, user.Id, SignupStatus.Pending);
        SeedShiftSignup(shift1.Id, Guid.NewGuid(), SignupStatus.Pending);
        SeedShiftSignup(shift1.Id, Guid.NewGuid(), SignupStatus.Confirmed); // not pending

        // Rota on inactive event â€” should NOT be counted
        var oldRota = SeedRota(team.Id, inactiveEvent.Id, "Old Shifts");
        var oldShift = SeedShift(oldRota.Id);
        SeedShiftSignup(oldShift.Id, user.Id, SignupStatus.Pending);

        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetAdminTeamListAsync(1, 500, TestContext.Current.CancellationToken);

        var summary = result.Teams.Should().ContainSingle(t => t.Name == "Dept A").Subject;
        summary.PendingShiftSignupCount.Should().Be(2);
    }

    [HumansFact]
    public async Task GetAdminTeamListAsync_ReturnsZeroPendingShifts_WhenNoActiveEvent()
    {
        var team = SeedTeam("Dept B");
        var user = SeedUser();
        SeedTeamMember(team.Id, user.Id);

        var inactiveEvent = SeedEventSettings("Past Event", isActive: false);
        var rota = SeedRota(team.Id, inactiveEvent.Id, "Shifts");
        var shift = SeedShift(rota.Id);
        SeedShiftSignup(shift.Id, user.Id, SignupStatus.Pending);

        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetAdminTeamListAsync(1, 500, TestContext.Current.CancellationToken);

        var summary = result.Teams.Should().ContainSingle(t => t.Name == "Dept B").Subject;
        summary.PendingShiftSignupCount.Should().Be(0);
    }

    // ==========================================================================
    // DeleteTeamAsync â€” #494: revoke Google access when a team is soft-deleted
    // ==========================================================================

    [HumansFact]
    public async Task DeleteTeamAsync_ClosesActiveMemberships()
    {
        // Arrange
        var team = SeedTeam("Doomed Team");
        var alice = SeedUser(displayName: "Alice");
        var bob = SeedUser(displayName: "Bob");
        SeedTeamMember(team.Id, alice.Id);
        SeedTeamMember(team.Id, bob.Id);
        // Previously-left member should NOT be touched.
        var ghost = SeedUser(displayName: "Ghost");
        SeedTeamMember(team.Id, ghost.Id, leftAt: Clock.GetCurrentInstant() - Duration.FromDays(30));
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var actor = Guid.NewGuid();
        // Act
        await _service.DeleteTeamAsync(team.Id, actor, TestContext.Current.CancellationToken);

        // Service uses its own DbContext; detach trackers so assertions re-read
        // from the store rather than returning stale tracked entities.
        ClearAllTrackers();

        // Assert â€” team soft-deleted
        var reloaded = await TeamsDb.Teams.AsNoTracking().FirstOrDefaultAsync(t => t.Id == team.Id, TestContext.Current.CancellationToken);
        reloaded!.IsActive.Should().BeFalse();

        // All previously-active memberships now have LeftAt set.
        var aliceMember = await TeamsDb.TeamMembers.AsNoTracking()
            .FirstAsync(tm => tm.TeamId == team.Id && tm.UserId == alice.Id, TestContext.Current.CancellationToken);
        var bobMember = await TeamsDb.TeamMembers.AsNoTracking()
            .FirstAsync(tm => tm.TeamId == team.Id && tm.UserId == bob.Id, TestContext.Current.CancellationToken);
        aliceMember.LeftAt.Should().NotBeNull();
        bobMember.LeftAt.Should().NotBeNull();
        aliceMember.LeftAt.Should().Be(Clock.GetCurrentInstant());
        bobMember.LeftAt.Should().Be(Clock.GetCurrentInstant());

        // Previously-left membership is unchanged (still has its original LeftAt).
        var ghostMember = await TeamsDb.TeamMembers.AsNoTracking()
            .FirstAsync(tm => tm.TeamId == team.Id && tm.UserId == ghost.Id, TestContext.Current.CancellationToken);
        ghostMember.LeftAt.Should().Be(Clock.GetCurrentInstant() - Duration.FromDays(30));

        foreach (var userId in new[] { alice.Id, bob.Id })
        {
            await AuditLog.Received(1).LogAsync(
                AuditAction.TeamMemberRemoved, nameof(Team), team.Id,
                $"Membership ended when {team.Name} was deactivated", actor,
                relatedEntityId: userId, relatedEntityType: nameof(User));
        }
        AuditLog.ReceivedCalls().Count(c => string.Equals(c.GetMethodInfo().Name, nameof(IAuditLogService.LogAsync), StringComparison.Ordinal))
            .Should().Be(2, "already-left memberships must not receive another removal audit");
        await _service.DeleteTeamAsync(team.Id, actor, TestContext.Current.CancellationToken);
        AuditLog.ReceivedCalls().Count(c => string.Equals(c.GetMethodInfo().Name, nameof(IAuditLogService.LogAsync), StringComparison.Ordinal)).Should().Be(2);

        // DeactivateResourcesForTeamAsync is intentionally NOT called from DeleteTeamAsync:
        // flipping GoogleResource.IsActive here would make the next reconciliation tick
        // skip the resources and leave stale Google access in place. Deactivation happens
        // in the sync service after access has been revoked.
        await _teamResourceService.DidNotReceive().DeactivateResourcesForTeamAsync(
            Arg.Any<Guid>(), Arg.Any<GoogleResourceType?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task DeleteTeamAsync_NoActiveMembers_StillSoftDeletes()
    {
        var team = SeedTeam("Empty Team");
        await SaveAllAsync(TestContext.Current.CancellationToken);

        await _service.DeleteTeamAsync(team.Id, Guid.NewGuid(), TestContext.Current.CancellationToken);

        ClearAllTrackers();

        var reloaded = await TeamsDb.Teams.AsNoTracking().FirstOrDefaultAsync(t => t.Id == team.Id, TestContext.Current.CancellationToken);
        reloaded!.IsActive.Should().BeFalse();

        await _teamResourceService.DidNotReceive().DeactivateResourcesForTeamAsync(
            Arg.Any<Guid>(), Arg.Any<GoogleResourceType?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetExpectedAsync_DuplicateGoogleGroupEmail_SkipsClaim()
    {
        var alice = SeedUser(displayName: "Alice");
        var bob = SeedUser(displayName: "Bob");
        var first = SeedTeam("First");
        var second = SeedTeam("Second");
        first.GoogleGroupPrefix = "shared";
        second.GoogleGroupPrefix = "shared";
        SeedTeamMember(first.Id, alice.Id);
        SeedTeamMember(second.Id, bob.Id);
        await SaveAllAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetExpectedAsync(ct: TestContext.Current.CancellationToken);

        result.Should().NotContainKey($"shared@{DomainConstants.GoogleGroupDomain}");
    }

    // --- Helpers ---

    private TeamRoleDefinition SeedTeamRoleDefinition(Guid teamId, bool isManagement)
    {
        var definition = new TeamRoleDefinition
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            Name = isManagement ? "Coordinator" : "Member Role",
            IsManagement = isManagement,
            SlotCount = 1,
            SortOrder = 0,
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        };
        TeamsDb.TeamRoleDefinitions.Add(definition);
        return definition;
    }

    private void SeedTeamRoleAssignment(Guid roleDefinitionId, Guid teamMemberId)
    {
        var assignment = new TeamRoleAssignment
        {
            Id = Guid.NewGuid(),
            TeamRoleDefinitionId = roleDefinitionId,
            TeamMemberId = teamMemberId,
            SlotIndex = 0,
            AssignedAt = Clock.GetCurrentInstant(),
            AssignedByUserId = Guid.NewGuid()
        };
        TeamsDb.TeamRoleAssignments.Add(assignment);
    }

    /// <summary>
    /// A fake <see cref="ISettingsService"/> resolving "the active event" off this
    /// test's own <see cref="TeamsTestHarness.ShiftsDb"/>-seeded rows (Shifts still
    /// keys its per-event knobs by <c>settings_event</c>'s id, so its own
    /// <c>EventSettings.IsActive</c> stands in for the real Settings-owned table
    /// here). Mirrors <c>ShiftsTestHarness.NewCalendarResolver</c>, which this test
    /// class cannot reach directly (it extends <see cref="TeamsTestHarness"/>).
    /// </summary>
    private ISettingsService NewSettingsServiceBackedByShiftsDb()
    {
        var settingsService = Substitute.For<ISettingsService>();
        settingsService.GetActiveEventSettingsAsync(Arg.Any<CancellationToken>())
            .Returns(async ci =>
            {
                var active = ShiftsDb.EventSettings.Local.FirstOrDefault(e => e.IsActive)
                    ?? await ShiftsDb.EventSettings.FirstOrDefaultAsync(e => e.IsActive, ci.Arg<CancellationToken>());
                return active is null ? null : ToEventSettingsInfo(active);
            });
        return settingsService;
    }

    private static EventSettingsInfo ToEventSettingsInfo(EventSettings src) => new(
        Id: src.Id,
        EventName: src.EventName,
        Year: src.Year,
        TimeZoneId: src.TimeZoneId,
        GateOpeningDate: src.GateOpeningDate,
        BuildStartOffset: src.BuildStartOffset,
        EventEndOffset: src.EventEndOffset,
        StrikeEndOffset: src.StrikeEndOffset,
        FirstCrewStartOffset: src.FirstCrewStartOffset,
        SetupWeekStartOffset: src.SetupWeekStartOffset,
        PreEventWeekStartOffset: src.PreEventWeekStartOffset,
        FinishingWeekendStartOffset: src.FinishingWeekendStartOffset,
        EarlyEntryCapacity: new Dictionary<int, int>(src.EarlyEntryCapacity),
        BarriosEarlyEntryAllocation: src.BarriosEarlyEntryAllocation is null
            ? null : new Dictionary<int, int>(src.BarriosEarlyEntryAllocation),
        EarlyEntryClose: src.EarlyEntryClose);

    private EventSettings SeedEventSettings(string name, bool isActive)
    {
        var es = new EventSettings
        {
            Id = Guid.NewGuid(),
            EventName = name,
            TimeZoneId = "Europe/Madrid",
            GateOpeningDate = new LocalDate(2026, 7, 1),
            BuildStartOffset = -7,
            EventEndOffset = 5,
            StrikeEndOffset = 7,
            IsActive = isActive,
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        };
        ShiftsDb.EventSettings.Add(es);
        return es;
    }

    private Rota SeedRota(Guid teamId, Guid eventSettingsId, string name)
    {
        var rota = new Rota
        {
            Id = Guid.NewGuid(),
            TeamId = teamId,
            EventSettingsId = eventSettingsId,
            Name = name,
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        };
        ShiftsDb.Rotas.Add(rota);
        return rota;
    }

    private Shift SeedShift(Guid rotaId)
    {
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            RotaId = rotaId,
            DayOffset = 0,
            StartTime = new LocalTime(9, 0),
            Duration = Duration.FromHours(4),
            MinVolunteers = 1,
            MaxVolunteers = 5,
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        };
        ShiftsDb.Shifts.Add(shift);
        return shift;
    }

    private void SeedShiftSignup(Guid shiftId, Guid userId, SignupStatus status)
    {
        var signup = new ShiftSignup
        {
            Id = Guid.NewGuid(),
            ShiftId = shiftId,
            UserId = userId,
            Status = status,
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        };
        ShiftsDb.ShiftSignups.Add(signup);
    }

}
