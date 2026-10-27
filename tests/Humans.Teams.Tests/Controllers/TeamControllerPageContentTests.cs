using Humans.Consent.Contracts;
using Xunit;
using System.Globalization;
using System.Security.Claims;
using Humans.Base.Constants;
using Humans.Base.Extensions;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using Microsoft.Extensions.DependencyInjection;
using AwesomeAssertions;
using Humans.Base;
using Humans.Base.Configuration;
using Humans.Base.Enums;
using Humans.GoogleIntegration.Contracts;
using Humans.Teams.Contracts;
using Humans.Teams.Controllers;
using Humans.Teams.Models;
using Humans.Teams.Services;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using NodaTime;
using NSubstitute;

namespace Humans.Teams.Tests.Controllers;

public class TeamControllerPageContentTests
{
    [HumansFact]
    public async Task Roster_OrdersUnsortedOwnerDataByPriorityTeamRoleAndSlot()
    {
        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var teams = Substitute.For<ITeamManagementService>();
        var pages = Substitute.For<ITeamPageService>();
        TeamRosterSlotSummary Slot(string team, string role, int number, SlotPriority priority) =>
            new(team, team.ToLowerInvariant(), role, null, Guid.NewGuid(), number,
                priority.ToString(), "", nameof(RolePeriod.Event), false, null, null, IsPublic: true);
        pages.GetRosterAsync("Important", "Open", "Event", Arg.Any<CancellationToken>()).Returns(new[]
        {
            Slot("Beta", "Lead", 1, SlotPriority.Important),
            Slot("Alpha", "Zulu", 1, SlotPriority.Important),
            Slot("Alpha", "Lead", 2, SlotPriority.Important),
            Slot("Zulu", "Lead", 1, SlotPriority.Critical),
            Slot("alpha", "Lead", 1, SlotPriority.Important),
            Slot("Alpha", "Lead", 3, SlotPriority.None)
        });
        teams.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new[] { "Alpha", "Beta", "Zulu" }
            .Select(name => new TeamInfo(Guid.NewGuid(), name, null, name.ToLowerInvariant(), true, false,
                SystemTeamType.None, false, false, false, false, Instant.FromUtc(2026, 1, 1, 0, 0), []))
            .ToDictionary(team => team.Id));
        var controller = new TeamController(
            teams, pages, Substitute.For<IUserService>(),
            Substitute.For<IConsentService>(), Substitute.For<ITeamResourceService>(), services.GetRequiredService<IStringLocalizer<TeamsResource>>(),
            services.GetRequiredService<IStringLocalizer<SharedResource>>(), new ConfigurationBuilder().Build(),
            new ConfigurationRegistry(), SystemClock.Instance, Substitute.For<IAuthorizationService>(),
            NullLogger<TeamController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.Roster("Important", "Open", "Event", TestContext.Current.CancellationToken);

        var model = result.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<RosterSummaryViewModel>().Which;
        model.Slots.Select(slot => (slot.TeamName, slot.RoleName, slot.SlotNumber)).Should().Equal(
            ("Zulu", "Lead", 1), ("alpha", "Lead", 1), ("Alpha", "Lead", 2),
            ("Alpha", "Zulu", 1), ("Beta", "Lead", 1), ("Alpha", "Lead", 3));
        model.PriorityFilter.Should().Be("Important");
        model.StatusFilter.Should().Be("Open");
        model.PeriodFilter.Should().Be("Event");
    }

    [HumansTheory]
    [InlineData("create", false, false)]
    [InlineData("create-group", false, false)]
    [InlineData("edit", false, false)]
    [InlineData("delete", false, false)]
    [InlineData("create", true, false)]
    [InlineData("create-database", true, false)]
    [InlineData("create-dependency", true, false)]
    [InlineData("edit", true, false)]
    [InlineData("edit-database", true, false)]
    [InlineData("edit-dependency", true, false)]
    [InlineData("edit-slug", false, false)]
    [InlineData("edit-custom", false, false)]
    [InlineData("edit-group", false, false)]
    [InlineData("delete", true, false)]
    [InlineData("delete", false, true)]
    public async Task TeamMutation_RejectionsKeepReasonWithoutStack_AndUnexpectedHandling(
        string action, bool unexpected, bool actorMissing)
    {
        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        var teams = Substitute.For<ITeamManagementService>();
        var logger = Substitute.For<ILogger<TeamController>>();
        var auth = Substitute.For<IAuthorizationService>();
        auth.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(AuthorizationResult.Success());
        var teamId = Guid.NewGuid();
        const string reason = "The team hierarchy does not permit this change";
        var editCase = action;
        if (action.StartsWith("edit-", StringComparison.Ordinal)) action = "edit";
        if (action.StartsWith("create-", StringComparison.Ordinal)) action = "create";
        var conflict = editCase switch
        {
            "edit-slug" => Humans.Teams.Data.TeamUpdateConflict.SlugTaken,
            "edit-custom" => Humans.Teams.Data.TeamUpdateConflict.CustomSlugTaken,
            "create-group" or "edit-group" => Humans.Teams.Data.TeamUpdateConflict.GroupPrefixTaken,
            _ => Humans.Teams.Data.TeamUpdateConflict.None
        };
        Exception failure = editCase switch
        {
            "create-database" or "edit-database" => new Microsoft.EntityFrameworkCore.DbUpdateException("Database unavailable", new Exception("CustomSlug is mentioned but is not a unique violation")),
            "create-dependency" or "edit-dependency" => new InvalidOperationException(reason),
            "delete" when unexpected => new InvalidOperationException("Private dependency failure"),
            _ => unexpected ? new IOException("Database unavailable") : new InvalidOperationException(reason)
        };
        teams.CreateTeamWithGoogleGroupAsync("", null, false)
            .ReturnsForAnyArgs(unexpected ? Task.FromException<TeamUpdateResult>(failure)
                : Task.FromResult(new TeamUpdateResult(Conflict: conflict, ErrorMessage: conflict == Humans.Teams.Data.TeamUpdateConflict.None ? reason : null)));
        teams.UpdateTeamWithGoogleGroupAsync(teamId, "", null, false, false)
            .ReturnsForAnyArgs(unexpected ? Task.FromException<TeamUpdateResult>(failure)
                : Task.FromResult(new TeamUpdateResult(Conflict: conflict, ErrorMessage: conflict == Humans.Teams.Data.TeamUpdateConflict.None ? reason : null)));
        teams.DeleteTeamAsync(teamId, Guid.Empty).ReturnsForAnyArgs(unexpected
            ? Task.FromException<TeamDeactivationResult>(failure)
            : Task.FromResult(new TeamDeactivationResult(reason)));
        teams.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>());
        var actor = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        if (!actorMissing)
            users.GetUserInfoAsync(actor, Arg.Any<CancellationToken>())
                .Returns(UserInfo.Create(new User { Id = actor, DisplayName = "Admin" }, [], [], [], null, []));
        var http = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, actor.ToString())], "test"))
        };
        var controller = new TeamController(
            teams, Substitute.For<ITeamPageService>(), users,
            Substitute.For<IConsentService>(), Substitute.For<ITeamResourceService>(), services.GetRequiredService<IStringLocalizer<TeamsResource>>(),
            services.GetRequiredService<IStringLocalizer<SharedResource>>(), new ConfigurationBuilder().Build(),
            new ConfigurationRegistry(), SystemClock.Instance, auth, logger)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
            Url = Substitute.For<IUrlHelper>()
        };
        TeamFormViewModelBase model = string.Equals(action, "create", StringComparison.Ordinal)
            ? new CreateTeamViewModel { Name = "Alpha" }
            : new EditTeamViewModel { Id = teamId, Name = "Alpha", IsActive = true };
        Task<IActionResult> MutateAsync() => action switch
        {
            "create" => controller.CreateTeam((CreateTeamViewModel)model),
            "edit" => controller.EditTeam(teamId, (EditTeamViewModel)model),
            _ => controller.DeleteTeam(teamId)
        };

        if (actorMissing)
        {
            (await controller.DeleteTeam(teamId)).Should().BeOfType<UnauthorizedResult>();
            await teams.DidNotReceiveWithAnyArgs().DeleteTeamAsync(default, default);
            return;
        }

        if (unexpected)
        {
            if (string.Equals(action, "create", StringComparison.Ordinal))
            {
                (await MutateAsync()).Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(model);
                var error = logger.ReceivedCalls().Should().ContainSingle(call =>
                    string.Equals(call.GetMethodInfo().Name, "Log", StringComparison.Ordinal)).Subject.GetArguments();
                error[0].Should().Be(LogLevel.Error);
                error[3].Should().BeSameAs(failure);
            }
            else
            {
                Func<Task> act = async () => await MutateAsync();
                (await act.Should().ThrowAsync<Exception>()).Which.Should().BeSameAs(failure);
                controller.ModelState.Should().BeEmpty();
                controller.TempData.Should().BeEmpty();
                logger.ReceivedCalls().Should().BeEmpty();
            }
            return;
        }

        var result = await MutateAsync();
        if (string.Equals(action, "delete", StringComparison.Ordinal))
            result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be(nameof(TeamController.Summary));
        else
            result.Should().BeOfType<ViewResult>().Which.Model.Should().BeSameAs(model);
        var expectedReason = conflict switch
        {
            Humans.Teams.Data.TeamUpdateConflict.SlugTaken => "This team URL is already in use by another team.",
            Humans.Teams.Data.TeamUpdateConflict.CustomSlugTaken => "This custom slug is already in use by another team.",
            Humans.Teams.Data.TeamUpdateConflict.GroupPrefixTaken => "This Google Group prefix is already in use by another team.",
            _ => reason
        };
        var expectedField = conflict switch
        {
            Humans.Teams.Data.TeamUpdateConflict.SlugTaken => "Name",
            Humans.Teams.Data.TeamUpdateConflict.CustomSlugTaken => "CustomSlug",
            Humans.Teams.Data.TeamUpdateConflict.GroupPrefixTaken => "GoogleGroupPrefix",
            _ => string.Empty
        };
        if (string.Equals(action, "edit", StringComparison.Ordinal) || conflict != Humans.Teams.Data.TeamUpdateConflict.None)
        {
            controller.ModelState[expectedField]!.Errors.Should().ContainSingle().Which.ErrorMessage.Should().Be(expectedReason);
            controller.TempData.Should().NotContainKey("SuccessMessage");
        }
        else
            controller.TempData["ErrorMessage"].Should().Be(reason);
        var args = logger.ReceivedCalls().Should().ContainSingle(call =>
            string.Equals(call.GetMethodInfo().Name, "Log", StringComparison.Ordinal)).Subject.GetArguments();
        args[0].Should().Be(LogLevel.Warning);
        args[3].Should().BeNull();
        args[2]!.ToString().Should().Contain(expectedReason);
        if (!string.Equals(action, "create", StringComparison.Ordinal))
            args[2]!.ToString().Should().Contain(teamId.ToString());
    }

    [HumansTheory]
    [InlineData(null)]
    [InlineData("coordinator")]
    [InlineData("parent")]
    [InlineData(RoleNames.Admin)]
    [InlineData(RoleNames.Board)]
    [InlineData(RoleNames.TeamsAdmin)]
    public async Task Roster_PrivateRoles_RequireTeamManagementAccess(string? role)
    {
        var viewer = Guid.NewGuid();
        var now = Instant.FromUtc(2026, 1, 1, 0, 0);
        var coordinator = new TeamMemberInfo(Guid.NewGuid(), viewer, "Coordinator", null, null, TeamMemberRole.Coordinator, now);
        var parent = new TeamInfo(Guid.NewGuid(), "Parent", null, "parent", true, false,
            SystemTeamType.None, false, false, false, false, now,
            string.Equals(role, "parent", StringComparison.Ordinal) ? [coordinator] : []);
        var child = parent with
        {
            Id = Guid.NewGuid(),
            Name = "Child",
            Slug = "child",
            ParentTeamId = parent.Id,
            Members = string.Equals(role, "coordinator", StringComparison.Ordinal) ? [coordinator] : []
        };
        var other = child with { Id = Guid.NewGuid(), Name = "Other", Slug = "other", ParentTeamId = null, Members = [] };
        var teams = Substitute.For<ITeamManagementService>();
        var pages = Substitute.For<ITeamPageService>();
        teams.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>
        {
            [parent.Id] = parent,
            [child.Id] = child,
            [other.Id] = other
        });
        var publicSlot = new TeamRosterSlotSummary(child.Name, child.Slug, "Public role", null,
            Guid.NewGuid(), 1, nameof(SlotPriority.Important), "", nameof(RolePeriod.Event), false, null, null, IsPublic: true);
        var privateSlot = publicSlot with { RoleDefinitionId = Guid.NewGuid(), RoleName = "Private role", IsPublic = false };
        var otherPrivate = privateSlot with { RoleDefinitionId = Guid.NewGuid(), TeamName = other.Name, TeamSlug = other.Slug };
        pages.GetRosterAsync(null, null, null, Arg.Any<CancellationToken>()).Returns(new[] { publicSlot, privateSlot, otherPrivate });
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, viewer.ToString()) };
        if (role is not null) claims.Add(new Claim(ClaimTypes.Role, role));
        var controller = new TeamController(
            teams, pages, Substitute.For<IUserService>(), Substitute.For<IConsentService>(), Substitute.For<ITeamResourceService>(),
            Substitute.For<IStringLocalizer<TeamsResource>>(), Substitute.For<IStringLocalizer<SharedResource>>(),
            new ConfigurationBuilder().Build(), new ConfigurationRegistry(), SystemClock.Instance,
            Substitute.For<IAuthorizationService>(), NullLogger<TeamController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) }
            }
        };

        var result = await controller.Roster(null, null, null, TestContext.Current.CancellationToken);

        var slots = result.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<RosterSummaryViewModel>().Which.Slots;
        var expected = role is RoleNames.Admin or RoleNames.Board or RoleNames.TeamsAdmin
            ? new[] { publicSlot.RoleDefinitionId, privateSlot.RoleDefinitionId, otherPrivate.RoleDefinitionId }
            : role is "coordinator" or "parent"
                ? new[] { publicSlot.RoleDefinitionId, privateSlot.RoleDefinitionId }
                : new[] { publicSlot.RoleDefinitionId };
        slots.Select(slot => slot.RoleDefinitionId).Should().BeEquivalentTo(expected);
    }

    [HumansTheory]
    [InlineData(null)]
    [InlineData(RoleNames.Admin)]
    [InlineData(RoleNames.Board)]
    [InlineData(RoleNames.TeamsAdmin)]
    public async Task MemberLists_HiddenPendingRequestsAndRoster_RequirePrivilegedRole(string? role)
    {
        var userId = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfo.Create(new User { Id = userId }, [], [], [], null, [])));
        var teams = Substitute.For<ITeamManagementService>();
        var pages = Substitute.For<ITeamPageService>();
        var hidden = new TeamInfo(Guid.NewGuid(), "Private", null, "private", true, false,
            SystemTeamType.None, true, false, true, false, Instant.FromUtc(2026, 1, 1, 0, 0), []);
        var visible = hidden with { Id = Guid.NewGuid(), Name = "Public", Slug = "public", IsHidden = false };
        teams.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>
        {
            [hidden.Id] = hidden,
            [visible.Id] = visible
        });
        var hiddenRequest = new TeamJoinRequestSnapshot(Guid.NewGuid(), hidden.Id, hidden.Name,
            userId, null, null, null, TeamJoinRequestStatus.Pending, null,
            Instant.FromUtc(2026, 10, 1, 0, 0), null, null, hidden.Slug);
        var visibleRequest = hiddenRequest with
        {
            Id = Guid.NewGuid(),
            TeamId = visible.Id,
            TeamName = visible.Name,
            TeamSlug = visible.Slug
        };
        teams.GetPendingRequestsForUserAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new[] { hiddenRequest, visibleRequest });
        var hiddenSlot = new TeamRosterSlotSummary(hidden.Name, hidden.Slug, "Lead", null,
            Guid.NewGuid(), 1, nameof(SlotPriority.Important), "", nameof(RolePeriod.Event), false, null, null, IsPublic: true);
        var visibleSlot = hiddenSlot with { TeamName = visible.Name, TeamSlug = visible.Slug };
        pages.GetRosterAsync(null, null, null, Arg.Any<CancellationToken>()).Returns(new[] { hiddenSlot, visibleSlot });
        pages.GetMyTeamMembershipsAsync(userId, Arg.Any<CancellationToken>()).Returns(Array.Empty<MyTeamMembershipSummary>());
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, userId.ToString()) };
        if (role is not null) claims.Add(new Claim(ClaimTypes.Role, role));
        var controller = new TeamController(
            teams, pages, users, Substitute.For<IConsentService>(), Substitute.For<ITeamResourceService>(),
            Substitute.For<IStringLocalizer<TeamsResource>>(), Substitute.For<IStringLocalizer<SharedResource>>(),
            new ConfigurationBuilder().Build(), new ConfigurationRegistry(), SystemClock.Instance,
            Substitute.For<IAuthorizationService>(), NullLogger<TeamController>.Instance)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test")) }
            }
        };

        var result = await controller.MyTeams(TestContext.Current.CancellationToken);

        var model = result.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<MyTeamsViewModel>().Which;
        model.PendingRequests.Select(request => request.Id).Should().Equal(role is null
            ? new[] { visibleRequest.Id } : new[] { hiddenRequest.Id, visibleRequest.Id });
        var rosterResult = await controller.Roster(null, null, null, TestContext.Current.CancellationToken);
        var roster = rosterResult.Should().BeOfType<ViewResult>().Which.Model.Should().BeOfType<RosterSummaryViewModel>().Which;
        roster.Slots.Select(slot => slot.TeamSlug).Should().Equal(role is null
            ? new[] { visible.Slug } : new[] { hidden.Slug, visible.Slug });
    }

    [HumansTheory]
    [InlineData("birthdays", "viewer")]
    [InlineData("my", "viewer")]
    [InlineData("my", "requests")]
    [InlineData("join", "viewer")]
    [InlineData("join", "team")]
    [InlineData("join", "pending")]
    public async Task Member_gets_cancel_at_each_read_boundary(string action, string boundary)
    {
        using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
        using var request = new CancellationTokenSource();
        var userId = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        var teams = Substitute.For<ITeamManagementService>();
        var team = new TeamInfo(Guid.NewGuid(), "Alpha", null, "alpha", true, false, SystemTeamType.None,
            false, false, false, false, Instant.MinValue, []);
        void CheckCancellation(string read, CancellationToken ct)
        {
            if (string.Equals(boundary, read, StringComparison.Ordinal))
                ct.ThrowIfCancellationRequested();
        }
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            CheckCancellation("viewer", call.Arg<CancellationToken>());
            return new ValueTask<UserInfo?>(UserInfo.Create(new User { Id = userId }, [], [], [], null, []));
        });
        teams.GetTeamBySlugAsync(team.Slug, Arg.Any<CancellationToken>()).Returns(call =>
        {
            CheckCancellation("team", call.Arg<CancellationToken>());
            return Task.FromResult<TeamInfo?>(team);
        });
        teams.GetUserPendingRequestAsync(team.Id, userId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            CheckCancellation("pending", call.Arg<CancellationToken>());
            return Task.FromResult<TeamJoinRequestSnapshot?>(null);
        });
        users.GetAllUserInfosAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<UserInfo>());
        teams.GetTeamsAsync(Arg.Any<CancellationToken>()).Returns(new Dictionary<Guid, TeamInfo>
        {
            [team.Id] = new(team.Id, team.Name, null, team.Slug, true, false,
                SystemTeamType.None, true, false, false, false, Instant.FromUtc(2026, 1, 1, 0, 0), [])
        });
        var pages = Substitute.For<ITeamPageService>();
        pages.GetMyTeamMembershipsAsync(userId, Arg.Any<CancellationToken>()).Returns(Array.Empty<MyTeamMembershipSummary>());
        var pendingRequest = new TeamJoinRequestSnapshot(Guid.NewGuid(), team.Id, team.Name,
            userId, null, null, null, TeamJoinRequestStatus.Pending, null,
            Instant.FromUtc(2026, 10, 1, 0, 0), null, null, team.Slug);
        teams.GetPendingRequestsForUserAsync(userId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            CheckCancellation("requests", call.Arg<CancellationToken>());
            return Task.FromResult<IReadOnlyList<TeamJoinRequestSnapshot>>([pendingRequest]);
        });
        var http = new DefaultHttpContext
        {
            RequestServices = services,
            RequestAborted = request.Token,
            Session = Substitute.For<ISession>(),
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"))
        };
        var controller = new TeamController(
            teams, pages, users,
            Substitute.For<IConsentService>(), Substitute.For<ITeamResourceService>(), services.GetRequiredService<IStringLocalizer<TeamsResource>>(),
            services.GetRequiredService<IStringLocalizer<SharedResource>>(), new ConfigurationBuilder().Build(),
            new ConfigurationRegistry(), SystemClock.Instance, Substitute.For<IAuthorizationService>(),
            NullLogger<TeamController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
            Url = Substitute.For<IUrlHelper>()
        };
        Task<IActionResult> ReadAsync() => action switch
        {
            "birthdays" => controller.Birthdays(1, request.Token),
            "my" => controller.MyTeams(request.Token),
            _ => controller.Join(team.Slug)
        };

        var view = (await ReadAsync()).Should().BeOfType<ViewResult>().Subject;
        if (string.Equals(action, "my", StringComparison.Ordinal))
        {
            var pending = view.Model.Should().BeOfType<MyTeamsViewModel>().Subject.PendingRequests
                .Should().ContainSingle().Subject;
            pending.Id.Should().Be(pendingRequest.Id);
            pending.TeamName.Should().Be(team.Name);
            pending.TeamSlug.Should().Be(team.Slug);
            pending.RequestedAt.Should().Be(pendingRequest.RequestedAt.ToDateTimeUtc());
            await teams.Received(1).GetPendingRequestsForUserAsync(userId, request.Token);
        }
        await request.CancelAsync();
        Func<Task> abandonedRead = async () => await ReadAsync();
        await abandonedRead.Should().ThrowAsync<OperationCanceledException>();
    }

    [HumansTheory]
    [InlineData("Join", false)]
    [InlineData("Join", true)]
    [InlineData("Leave", false)]
    [InlineData("Leave", true)]
    [InlineData("Withdraw", false)]
    [InlineData("Withdraw", true)]
    public async Task MemberActionErrors_LocalizeRefusalsAndPropagateDependencyFailures(string action, bool unknown)
    {
        var originalCulture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("es");
        try
        {
            using var services = new ServiceCollection().AddLogging().AddLocalization().BuildServiceProvider();
            var userId = Guid.NewGuid();
            var users = Substitute.For<IUserService>();
            users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
                .Returns(UserInfo.Create(new User { Id = userId }, [], [], [], null, []));
            var team = new TeamInfo(Guid.NewGuid(), "Alpha", null, "alpha", true, false, SystemTeamType.None,
                false, false, false, false, Instant.MinValue, []);
            var teams = Substitute.For<ITeamManagementService>();
            teams.GetTeamBySlugAsync(team.Slug, Arg.Any<CancellationToken>()).Returns(team);
            var key = unknown ? "Teams_NotMember" : action switch
            {
                "Join" => "Teams_Team_AlreadyPendingRequest",
                "Leave" => "Teams_NotMember",
                _ => "Teams_RequestUnavailable"
            };
            teams.JoinTeamAsync(team.Id, userId, Arg.Any<string?>(), Arg.Any<CancellationToken>())
                .Returns(unknown
                    ? Task.FromException<TeamJoinResult>(new InvalidOperationException(key))
                    : Task.FromResult(new TeamJoinResult(ErrorKey: key)));
            teams.LeaveTeamAsync(team.Id, userId, Arg.Any<CancellationToken>())
                .Returns(unknown
                    ? Task.FromException<TeamMembershipMutationResult>(new InvalidOperationException(key))
                    : Task.FromResult(new TeamMembershipMutationResult(false, key)));
            teams.WithdrawJoinRequestAsync(Arg.Any<Guid>(), userId, Arg.Any<CancellationToken>())
                .Returns(unknown
                    ? Task.FromException<TeamMembershipMutationResult>(new InvalidOperationException(key))
                    : Task.FromResult(new TeamMembershipMutationResult(false, key)));
            var http = new DefaultHttpContext
            {
                RequestServices = services,
                User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"))
            };
            var controller = new TeamController(
                teams, Substitute.For<ITeamPageService>(), users,
                Substitute.For<IConsentService>(), Substitute.For<ITeamResourceService>(), services.GetRequiredService<IStringLocalizer<TeamsResource>>(),
                services.GetRequiredService<IStringLocalizer<SharedResource>>(), new ConfigurationBuilder().Build(),
                new ConfigurationRegistry(), Substitute.For<IClock>(), Substitute.For<IAuthorizationService>(),
                NullLogger<TeamController>.Instance)
            {
                ControllerContext = new ControllerContext { HttpContext = http },
                TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
                Url = Substitute.For<IUrlHelper>()
            };

            Task<IActionResult> ActAsync() => action switch
            {
                "Join" => controller.Join(team.Slug, new JoinTeamViewModel { TeamId = team.Id }),
                "Leave" => controller.Leave(team.Slug),
                _ => controller.WithdrawRequest(Guid.NewGuid())
            };

            if (unknown)
            {
                Func<Task> act = async () => await ActAsync();
                await act.Should().ThrowAsync<InvalidOperationException>().WithMessage(key);
                controller.TempData.Should().BeEmpty();
                return;
            }

            (await ActAsync()).Should().BeOfType<RedirectToActionResult>();
            controller.TempData[TempDataKeys.ErrorMessage].Should().Be(action switch
                {
                    "Join" => "Ya tiene una solicitud pendiente para este equipo.",
                    "Leave" => "No formas parte de este equipo.",
                    _ => "Esta solicitud para unirte no está disponible o ya no está pendiente."
                });
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    [HumansTheory]
    [InlineData("en")]
    [InlineData("es")]
    [InlineData("de")]
    [InlineData("it")]
    [InlineData("fr")]
    [InlineData("ca")]
    public async Task Join_InvalidMessageReturnsTheFormWithoutSubmittingARequest(string culture)
    {
        using var cultureScope = new CultureScope(culture);
        var registrations = new ServiceCollection().AddLogging().AddLocalization();
        registrations.AddControllers().AddDataAnnotationsLocalization(options =>
            options.DataAnnotationLocalizerProvider = (_, factory) => factory.Create(typeof(SharedResource)));
        using var services = registrations.BuildServiceProvider();
        var userId = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(UserInfo.Create(new User { Id = userId }, [], [], [], null, []));
        var team = new TeamInfo(Guid.NewGuid(), "Alpha", null, "alpha", true, false, SystemTeamType.None,
            true, false, false, false, Instant.MinValue, []);
        var teams = Substitute.For<ITeamManagementService>();
        teams.GetTeamBySlugAsync(team.Slug, Arg.Any<CancellationToken>()).Returns(_ => team);
        var http = new DefaultHttpContext
        {
            RequestServices = services,
            User = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"))
        };
        var controller = new TeamController(
            teams, Substitute.For<ITeamPageService>(), users,
            Substitute.For<IConsentService>(), Substitute.For<ITeamResourceService>(), services.GetRequiredService<IStringLocalizer<TeamsResource>>(),
            services.GetRequiredService<IStringLocalizer<SharedResource>>(), new ConfigurationBuilder().Build(),
            new ConfigurationRegistry(), SystemClock.Instance, Substitute.For<IAuthorizationService>(),
            NullLogger<TeamController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
            Url = Substitute.For<IUrlHelper>()
        };
        var model = new JoinTeamViewModel
        {
            TeamId = team.Id,
            TeamName = "Spoofed",
            TeamSlug = "wrong",
            RequiresApproval = false,
            Message = new string('x', 2001)
        };
        services.GetRequiredService<IObjectModelValidator>().Validate(controller.ControllerContext, null, "", model);

        var result = await controller.Join(team.Slug, model);

        var returned = result.Should().BeOfType<ViewResult>().Subject.Model
            .Should().BeOfType<JoinTeamViewModel>().Subject;
        returned.Message.Should().Be(model.Message);
        returned.TeamName.Should().Be(team.Name);
        returned.TeamSlug.Should().Be(team.Slug);
        returned.RequiresApproval.Should().BeTrue();
        var expected = services.GetRequiredService<IStringLocalizer<SharedResource>>()
            ["Validation_MaxLength", "", 2000];
        expected.ResourceNotFound.Should().BeFalse();
        controller.ModelState[nameof(model.Message)]!.Errors.Should().ContainSingle()
            .Which.ErrorMessage.Should().Be(expected.Value);
        await teams.DidNotReceive().JoinTeamAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

        teams.JoinTeamAsync(team.Id, userId, Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new TeamJoinResult(TeamJoinOutcome.Joined));
        controller.ModelState.Clear();
        model.Message = new string('x', 2000);
        services.GetRequiredService<IObjectModelValidator>().Validate(controller.ControllerContext, null, "", model);
        controller.ModelState.IsValid.Should().BeTrue();
        (await controller.Join(team.Slug, model)).Should().BeOfType<RedirectToActionResult>();
        await teams.Received(1).JoinTeamAsync(team.Id, userId, model.Message, Arg.Any<CancellationToken>());

        controller.ModelState.AddModelError(nameof(model.Message), "invalid");
        team = team with { IsSystemTeam = true, SystemTeamType = SystemTeamType.Volunteers };
        (await controller.Join(team.Slug, model)).Should().BeOfType<RedirectToActionResult>();
        controller.TempData[TempDataKeys.ErrorMessage].Should().Be(
            services.GetRequiredService<IStringLocalizer<TeamsResource>>()["Teams_Team_CannotJoinSystem"].Value);
        team = team with { IsSystemTeam = false, SystemTeamType = SystemTeamType.None, IsHidden = true };
        (await controller.Join(team.Slug, model)).Should().BeOfType<NotFoundResult>();
        model.TeamId = Guid.NewGuid();
        (await controller.Join(team.Slug, model)).Should().BeOfType<BadRequestResult>();
        await teams.Received(1).JoinTeamAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

        team = team with { IsHidden = false, IsActive = false };
        model.TeamId = team.Id;
        controller.ModelState.Clear();
        (await controller.Join(team.Slug)).Should().BeOfType<NotFoundResult>();
        (await controller.Join(team.Slug, model)).Should().BeOfType<NotFoundResult>();
        await teams.Received(1).JoinTeamAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task Details_RendersPageContentWithSharedMarkdownProtections()
    {
        const string markdown = """
            <p style="background-image:url(https://tracker.example/pixel)">Hello</p>
            <img src="http://insecure.example/pixel" />
            <img src="https://secure.example/photo" />

            - [x] Complete
            """;
        var team = new TeamPageTeamSummary(
            Guid.NewGuid(), "Test", "Test", null, "test", true, false, false,
            SystemTeamType.None, Instant.FromUtc(2026, 1, 1, 0, 0), true, false,
            markdown, [], null, null, null);
        var page = new TeamPageDetailResult(
            team, [], [], [], [], false, false, false, false, false, false, false,
            null, 0, null, null, [], []);
        var pages = Substitute.For<ITeamPageService>();
        pages.GetTeamPageDetailAsync("test", null, false, Arg.Any<CancellationToken>()).Returns(page);
        var controller = new TeamController(
            Substitute.For<ITeamManagementService>(), pages, Substitute.For<IUserService>(),
            Substitute.For<IConsentService>(), Substitute.For<ITeamResourceService>(), Substitute.For<IStringLocalizer<TeamsResource>>(),
            Substitute.For<IStringLocalizer<SharedResource>>(), new ConfigurationBuilder().Build(),
            new ConfigurationRegistry(), SystemClock.Instance, Substitute.For<IAuthorizationService>(),
            NullLogger<TeamController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() }
        };

        var result = await controller.Details("test", TestContext.Current.CancellationToken);

        var model = result.Should().BeOfType<ViewResult>().Subject.Model
            .Should().BeOfType<TeamDetailViewModel>().Subject;
        model.PageContentHtml.Should().NotContain("style=").And.NotContain("http://insecure.example");
        model.PageContentHtml.Should().Contain("https://secure.example/photo");
        model.PageContentHtml.Should().Contain("type=\"checkbox\"").And.Contain("checked").And.Contain("disabled");
    }
}
