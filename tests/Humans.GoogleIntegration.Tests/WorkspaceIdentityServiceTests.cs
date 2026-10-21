using System.Reflection;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Base.Authorization;
using Humans.Base.Enums;
using Humans.Base.Interfaces;
using Humans.GoogleIntegration.Contracts;
using Humans.GoogleIntegration.Controllers;
using Humans.GoogleIntegration.Services;
using Humans.GoogleIntegration.Tests.Infrastructure;
using Humans.GoogleIntegration.ViewComponents;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewComponents;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;

namespace Humans.GoogleIntegration.Tests;

/// <summary>
/// <see cref="WorkspaceIdentityService"/>: the read-time resolution that replaced Users'
/// <c>EnsureGoogleInvariantAsync</c>, the writes, the <c>google.workspace-identities</c> copy, and
/// the GDPR / merge hooks of <c>google_workspace_identities</c> (Users decomposition commit 15).
/// </summary>
public sealed class WorkspaceIdentityServiceTests
{
    private static CancellationToken Ct => Xunit.TestContext.Current.CancellationToken;

    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly InMemoryWorkspaceIdentityRepository _repo = new();
    private readonly Dictionary<Guid, UserInfo> _infos = [];
    private readonly WorkspaceIdentityService _sut;

    public WorkspaceIdentityServiceTests()
    {
        _users.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => new ValueTask<UserInfo?>(_infos.GetValueOrDefault(ci.Arg<Guid>())));
        _users.GetUserInfosAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(ci.Arg<IReadOnlyCollection<Guid>>()
                .Where(_infos.ContainsKey).ToDictionary(id => id, id => _infos[id])));
        _users.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyCollection<UserInfo>>(_infos.Values.Distinct().ToList()));
        _sut = WorkspaceIdentityFakes.Create(_users, _repo, _audit);
    }

    private sealed record Addr(
        string Email, bool Primary = false, bool Google = false, bool Verified = true,
        LegacyGoogleEmailStatus Status = LegacyGoogleEmailStatus.Unknown);

    private UserInfo Seed(params Addr[] addresses)
    {
        var id = Guid.NewGuid();
        var user = new User { Id = id, DisplayName = "Alice", BurnerName = "Alice", CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0) };
        var rows = addresses.Select(a => new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = id,
            Email = a.Email,
            IsVerified = a.Verified,
            IsPrimary = a.Primary,
            IsGoogle = a.Google,
            GoogleEmailStatus = a.Status,
        }).ToList();
        var info = user.ToUserInfo(rows);
        _infos[id] = info;
        return info;
    }

    // ─── Resolution parity with today's IsGoogle invariant ──────────────────────
    // For a member whose data satisfies the invariant EnsureGoogleInvariantAsync kept (exactly one
    // verified IsGoogle row, the @nobodies.team one when there is one), the effective identity is
    // that same address, with that row's status — before and after the copy.

    public static Xunit.TheoryData<string, string, LegacyGoogleEmailStatus> ParityCases => new()
    {
        { "nobodies", "alice@nobodies.team", LegacyGoogleEmailStatus.Valid },
        { "designated-personal", "alice@gmail.com", LegacyGoogleEmailStatus.Rejected },
    };

    [HumansTheory]
    [Xunit.MemberData(nameof(ParityCases))]
    public async Task InvariantHolder_ResolvesToTheIsGoogleAddress_BeforeAndAfterTheCopy(
        string scenario, string googleAddress, LegacyGoogleEmailStatus status)
    {
        _ = scenario;
        var info = Seed(
            new Addr("alice@example.org", Primary: true),
            new Addr(googleAddress, Google: true, Status: status),
            new Addr("zz-other@example.org"));

        var legacy = await _sut.GetWorkspaceIdentityAsync(info.Id, Ct);
        legacy.Should().Be(new WorkspaceIdentity(info.Id, googleAddress, (GoogleEmailStatus)(int)status));

        (await _sut.CopyLegacyIdentitiesAsync(Ct)).Should().Be(1);
        _repo.Rows[info.Id].Email.Should().Be(googleAddress);
        (await _sut.GetWorkspaceIdentityAsync(info.Id, Ct)).Should().Be(legacy);
    }

    [HumansFact]
    public async Task NoDesignation_FallsBackToTheVerifiedPrimary()
    {
        var info = Seed(
            new Addr("aaa@example.org"),
            new Addr("alice@example.org", Primary: true),
            new Addr("unverified@nobodies.team", Verified: false));

        (await _sut.GetWorkspaceIdentityAsync(info.Id, Ct)).Should()
            .Be(new WorkspaceIdentity(info.Id, "alice@example.org", GoogleEmailStatus.Unknown));
    }

    [HumansFact]
    public async Task NoDesignation_PrefersAVerifiedNobodiesAddressOverThePrimary()
    {
        var info = Seed(new Addr("alice@example.org", Primary: true), new Addr("alice@nobodies.team"));

        (await _sut.GetWorkspaceIdentityAsync(info.Id, Ct))!.Email.Should().Be("alice@nobodies.team");
    }

    [HumansFact]
    public async Task LegacyOnly_NoRow_ReadsTheFrozenFlags_AndARowOverridesThem()
    {
        var info = Seed(
            new Addr("alice@example.org", Primary: true),
            new Addr("alice@gmail.com", Google: true, Status: LegacyGoogleEmailStatus.Valid));
        _repo.Rows.Should().BeEmpty();

        (await _sut.GetWorkspaceIdentityAsync(info.Id, Ct)).Should()
            .Be(new WorkspaceIdentity(info.Id, "alice@gmail.com", GoogleEmailStatus.Valid));

        await _repo.UpsertAsync(info.Id, "alice@example.org", GoogleEmailStatus.Rejected, Instant.MinValue, Ct);
        (await _sut.GetWorkspaceIdentityAsync(info.Id, Ct)).Should()
            .Be(new WorkspaceIdentity(info.Id, "alice@example.org", GoogleEmailStatus.Rejected));
    }

    [HumansFact]
    public async Task NoVerifiedAddress_HasNoIdentity()
    {
        var info = Seed(new Addr("alice@example.org", Primary: true, Google: true, Verified: false));

        (await _sut.GetWorkspaceIdentityAsync(info.Id, Ct)).Should().BeNull();
    }

    [HumansFact]
    public async Task BulkRead_KeysByTheRequestedId()
    {
        var info = Seed(new Addr("alice@nobodies.team", Primary: true));
        var archivedId = Guid.NewGuid();
        _infos[archivedId] = info;

        var result = await _sut.GetWorkspaceIdentitiesAsync([archivedId, Guid.NewGuid()], Ct);

        result.Should().ContainSingle().Which.Should().Be(new KeyValuePair<Guid, WorkspaceIdentity>(
            archivedId, new WorkspaceIdentity(info.Id, "alice@nobodies.team", GoogleEmailStatus.Unknown)));
    }

    // ─── ClearIfAddress ──────────────────────────────────────────────────────────

    [HumansFact]
    public async Task ClearIfAddress_ThenLegacyFallback_DoesNotResurrectTheAddress()
    {
        // The frozen IsGoogle flag still names the address (Users no longer writes it), so only the
        // cleared row keeps the read from falling back to it.
        var info = Seed(
            new Addr("alice@example.org", Primary: true),
            new Addr("alice@gmail.com", Google: true, Status: LegacyGoogleEmailStatus.Valid));

        await _sut.ClearIfAddressAsync(info.Id, "ALICE@gmail.com", Ct);

        _repo.Rows[info.Id].Email.Should().BeNull();
        (await _sut.GetWorkspaceIdentityAsync(info.Id, Ct)).Should()
            .Be(new WorkspaceIdentity(info.Id, "alice@example.org", GoogleEmailStatus.Unknown));
        (await _sut.GetUncopiedLegacyIdentitiesAsync(Ct)).Should().BeEmpty();
    }

    [HumansFact]
    public async Task ClearIfAddress_OtherAddress_LeavesTheIdentity()
    {
        var info = Seed(new Addr("alice@example.org", Primary: true), new Addr("alice@gmail.com", Google: true));
        await _repo.UpsertAsync(info.Id, "alice@gmail.com", GoogleEmailStatus.Valid, Instant.MinValue, Ct);

        await _sut.ClearIfAddressAsync(info.Id, "alice@example.org", Ct);

        _repo.Rows[info.Id].Email.Should().Be("alice@gmail.com");
        _repo.Rows[info.Id].Status.Should().Be(GoogleEmailStatus.Valid);
    }

    // ─── Set ─────────────────────────────────────────────────────────────────────

    [HumansFact]
    public async Task Set_DesignatesAVerifiedAddress_AndAuditsTheChange()
    {
        var info = Seed(new Addr("alice@example.org", Primary: true), new Addr("alice@gmail.com"));
        var actor = Guid.NewGuid();
        var targetRowId = info.UserEmails.Single(e => string.Equals(e.Email, "alice@gmail.com", StringComparison.Ordinal)).Id;

        (await _sut.SetAsync(info.Id, "alice@gmail.com", actor, Ct)).Should().BeTrue();

        (await _sut.GetWorkspaceIdentityAsync(info.Id, Ct)).Should()
            .Be(new WorkspaceIdentity(info.Id, "alice@gmail.com", GoogleEmailStatus.Unknown));
        await _audit.Received(1).LogAsync(
            AuditAction.UserEmailGoogleSet, nameof(User), info.Id,
            "Set Google identity to alice@gmail.com (was alice@example.org)", actor,
            relatedEntityId: targetRowId, relatedEntityType: nameof(UserEmail));
    }

    [HumansFact]
    public async Task Set_RefusesUnverified_UnknownAndLockedAddresses()
    {
        var info = Seed(
            new Addr("alice@example.org", Primary: true),
            new Addr("pending@example.org", Verified: false),
            new Addr("alice@nobodies.team"));

        (await _sut.SetAsync(info.Id, "pending@example.org", info.Id, Ct)).Should().BeFalse();
        (await _sut.SetAsync(info.Id, "stranger@example.org", info.Id, Ct)).Should().BeFalse();
        // A verified @nobodies.team address locks the identity to the Workspace account.
        (await _sut.SetAsync(info.Id, "alice@example.org", info.Id, Ct)).Should().BeFalse();

        _repo.Rows.Should().BeEmpty();
        await _audit.DidNotReceiveWithAnyArgs().LogAsync(default, default!, default, default!, default(Guid));
    }

    [HumansFact]
    public async Task Set_ForAMergedAwayId_IsRefused()
    {
        var info = Seed(new Addr("alice@example.org", Primary: true));
        var archivedId = Guid.NewGuid();
        _infos[archivedId] = info;

        (await _sut.SetAsync(archivedId, "alice@example.org", info.Id, Ct)).Should().BeFalse();
        _repo.Rows.Should().BeEmpty();
    }

    [HumansTheory]
    [Xunit.InlineData(GoogleEmailStatus.Valid, GoogleEmailStatus.Valid)]
    [Xunit.InlineData(GoogleEmailStatus.Rejected, GoogleEmailStatus.Unknown)]
    public async Task Set_SameAddress_KeepsItsVerdict_ExceptARejectionWhichRetries(
        GoogleEmailStatus before, GoogleEmailStatus after)
    {
        var info = Seed(new Addr("alice@example.org", Primary: true));
        await _repo.UpsertAsync(info.Id, "alice@example.org", before, Instant.MinValue, Ct);

        (await _sut.SetAsync(info.Id, "alice@example.org", info.Id, Ct)).Should().BeTrue();

        _repo.Rows[info.Id].Status.Should().Be(after);
    }

    // ─── SetStatusFromSync ───────────────────────────────────────────────────────

    [HumansFact]
    public async Task SetStatusFromSync_WritesTheVerdictForTheCurrentIdentity()
    {
        var info = Seed(new Addr("alice@example.org", Primary: true, Google: true));

        (await _sut.SetStatusFromSyncAsync(info.Id, "Alice@Example.org", GoogleEmailStatus.Rejected, Ct)).Should().BeTrue();

        (await _sut.GetWorkspaceIdentityAsync(info.Id, Ct))!.Status.Should().Be(GoogleEmailStatus.Rejected);
    }

    [HumansFact]
    public async Task SetStatusFromSync_ForAnAddressNoLongerTheIdentity_IsDropped()
    {
        var info = Seed(new Addr("alice@example.org", Primary: true), new Addr("alice@gmail.com"));

        (await _sut.SetStatusFromSyncAsync(info.Id, "alice@gmail.com", GoogleEmailStatus.Rejected, Ct)).Should().BeFalse();

        _repo.Rows.Should().BeEmpty();
    }

    [HumansFact]
    public async Task SetStatusFromSync_ValidNeverOverwritesRejected()
    {
        var info = Seed(new Addr("alice@example.org", Primary: true));
        await _repo.UpsertAsync(info.Id, "alice@example.org", GoogleEmailStatus.Rejected, Instant.MinValue, Ct);

        (await _sut.SetStatusFromSyncAsync(info.Id, "alice@example.org", GoogleEmailStatus.Valid, Ct)).Should().BeFalse();

        _repo.Rows[info.Id].Status.Should().Be(GoogleEmailStatus.Rejected);
    }

    // ─── google.workspace-identities ─────────────────────────────────────────────

    [HumansFact]
    public async Task Copy_TakesOnlyMembersWithLegacyState_AndIsIdempotent()
    {
        var flagged = Seed(new Addr("a@example.org", Primary: true, Google: true));
        var verdictOnly = Seed(new Addr("b@example.org", Primary: true, Status: LegacyGoogleEmailStatus.Rejected));
        var untouched = Seed(new Addr("c@example.org", Primary: true));
        var alreadyCopied = Seed(new Addr("d@example.org", Primary: true, Google: true));
        await _repo.UpsertAsync(alreadyCopied.Id, "d@example.org", GoogleEmailStatus.Valid, Instant.MinValue, Ct);

        (await _sut.GetUncopiedLegacyIdentitiesAsync(Ct)).Select(r => r.UserId).Should()
            .BeEquivalentTo([flagged.Id, verdictOnly.Id]);
        (await _sut.CopyLegacyIdentitiesAsync(Ct)).Should().Be(2);
        (await _sut.CopyLegacyIdentitiesAsync(Ct)).Should().Be(0);

        _repo.Rows[verdictOnly.Id].Status.Should().Be(GoogleEmailStatus.Rejected);
        _repo.Rows.Should().NotContainKey(untouched.Id);
        _repo.Rows[alreadyCopied.Id].Status.Should().Be(GoogleEmailStatus.Valid);
    }

    [HumansFact]
    public async Task DataMigration_IsRegistered_AndRunsTheCopy()
    {
        Seed(new Addr("a@example.org", Primary: true, Google: true));
        var services = new ServiceCollection().AddSingleton<IWorkspaceIdentityManagement>(_sut).BuildServiceProvider();
        var migration = new SectionDataMigrations().Migrations().Should().ContainSingle().Subject;

        migration.Key.Should().Be("google.workspace-identities");
        (await migration.Status(services, Ct)).Pending.Should().Be(1);
        (await migration.Apply(services, Guid.NewGuid(), Ct)).Should().Be(1);
        (await migration.Status(services, Ct)).Pending.Should().Be(0);
    }

    // ─── Merge, export, erasure ──────────────────────────────────────────────────

    [HumansFact]
    public async Task Merge_DeletesTheArchivedRow_AndKeepsTheSurvivors()
    {
        var source = Guid.NewGuid();
        var target = Guid.NewGuid();
        await _repo.UpsertAsync(source, "old@example.org", GoogleEmailStatus.Valid, Instant.MinValue, Ct);
        await _repo.UpsertAsync(target, "new@example.org", GoogleEmailStatus.Valid, Instant.MinValue, Ct);

        await _sut.ReassignAsync(source, target, Guid.NewGuid(), Instant.MinValue, Ct);

        _repo.Rows.Keys.Should().BeEquivalentTo([target]);
    }

    [HumansFact]
    public async Task Export_CarriesTheIdentity_AndErasureDeletesTheRow()
    {
        var info = Seed(new Addr("alice@example.org", Primary: true));
        await _repo.UpsertAsync(info.Id, "alice@example.org", GoogleEmailStatus.Rejected, Instant.MinValue, Ct);

        var slice = (await _sut.ContributeForUserAsync(info.Id, Ct)).Should().ContainSingle().Subject;
        slice.SectionName.Should().Be(WorkspaceIdentityService.WorkspaceIdentitySlice);
        slice.Data.Should().BeEquivalentTo(new { Email = "alice@example.org", Status = "Rejected" });
        _sut.ErasureDeclaration.Should().ContainKey(WorkspaceIdentityService.WorkspaceIdentitySlice);

        await _sut.EraseForUserAsync(info.Id, Ct);
        _repo.Rows.Should().BeEmpty();
    }

    // ─── Surfaces ────────────────────────────────────────────────────────────────

    [HumansFact]
    public void Controller_SetRoutesAreAntiforgery_AndTheAdminRouteNeedsHumanAdminBoardOrAdmin()
    {
        var type = typeof(WorkspaceIdentityController);
        type.GetCustomAttribute<AuthorizeAttribute>().Should().NotBeNull();

        var set = type.GetMethod(nameof(WorkspaceIdentityController.Set))!;
        set.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>().Should().NotBeNull();
        set.GetCustomAttribute<HttpPostAttribute>()!.Template.Should().Be("WorkspaceEmail");
        set.GetCustomAttributes<AuthorizeAttribute>().Should().BeEmpty();

        var adminSet = type.GetMethod(nameof(WorkspaceIdentityController.AdminSet))!;
        adminSet.GetCustomAttribute<ValidateAntiForgeryTokenAttribute>().Should().NotBeNull();
        adminSet.GetCustomAttribute<HttpPostAttribute>()!.Template.Should().Be("Admin/{id:guid}/WorkspaceEmail");
        adminSet.GetCustomAttribute<AuthorizeAttribute>()!.Policy.Should().Be(PolicyNames.HumanAdminBoardOrAdmin);
    }

    private WorkspaceIdentityController Controller(IWorkspaceIdentityManagement identities, Guid currentUserId)
    {
        var localizer = Substitute.For<Microsoft.Extensions.Localization.IStringLocalizer<GoogleIntegrationResource>>();
        localizer[Arg.Any<string>()].Returns(ci => new Microsoft.Extensions.Localization.LocalizedString(ci.Arg<string>(), ci.Arg<string>()));
        var http = new DefaultHttpContext
        {
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                [new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, currentUserId.ToString())], "Test")),
        };
        return new WorkspaceIdentityController(_users, identities, localizer,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<WorkspaceIdentityController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = http },
            TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>()),
        };
    }

    [HumansFact]
    public async Task SetRoute_DesignatesTheCurrentUsersRow_AndReturnsToTheGrid()
    {
        var info = Seed(new Addr("alice@example.org", Primary: true), new Addr("alice@gmail.com"));
        var identities = Substitute.For<IWorkspaceIdentityManagement>();
        identities.SetAsync(info.Id, "alice@gmail.com", info.Id, Arg.Any<CancellationToken>()).Returns(true);
        var rowId = info.UserEmails.Single(e => string.Equals(e.Email, "alice@gmail.com", StringComparison.Ordinal)).Id;

        var result = await Controller(identities, info.Id).Set(rowId, Ct);

        result.Should().BeOfType<RedirectToActionResult>().Which.ActionName.Should().Be("Emails");
        await identities.Received(1).SetAsync(info.Id, "alice@gmail.com", info.Id, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task AdminSetRoute_ForAMergedAwayId_IsNotFound_AndAnotherUsersRowIsIgnored()
    {
        var target = Seed(new Addr("bob@example.org", Primary: true));
        var archivedId = Guid.NewGuid();
        _infos[archivedId] = target;
        var admin = Seed(new Addr("admin@example.org", Primary: true));
        var identities = Substitute.For<IWorkspaceIdentityManagement>();
        var controller = Controller(identities, admin.Id);

        (await controller.AdminSet(archivedId, target.UserEmails[0].Id, Ct)).Should().BeOfType<NotFoundResult>();
        // An email id that is not the target's own row never reaches the service.
        (await controller.AdminSet(target.Id, admin.UserEmails[0].Id, Ct)).Should().BeOfType<RedirectToActionResult>();
        await identities.DidNotReceiveWithAnyArgs().SetAsync(default, default!, default, default);
    }

    [HumansTheory]
    [Xunit.InlineData(ProfileCardViewMode.Public)]
    [Xunit.InlineData(ProfileCardViewMode.Self)]
    public async Task Card_RendersNothingOutsideTheAdminView(ProfileCardViewMode mode)
    {
        var identities = Substitute.For<IGoogleIntegrationService>();
        var sut = new WorkspaceIdentityCardViewComponent(identities, Substitute.For<IAuthorizationService>())
        {
            ViewComponentContext = new ViewComponentContext
            {
                ViewContext = new ViewContext { HttpContext = new DefaultHttpContext() },
            },
        };

        var result = await sut.InvokeAsync(Guid.NewGuid(), mode);

        result.Should().BeOfType<ContentViewComponentResult>().Which.Content.Should().BeEmpty();
        await identities.DidNotReceiveWithAnyArgs().GetWorkspaceIdentityAsync(default, default);
    }

    [HumansFact]
    public void UserParts_ContributeTheCardsToTheAdminDetailSidebar()
    {
        IUserPart contribution = new SectionUserParts();

        contribution.Parts().Should().ContainSingle().Which.Slot.Should().Be(UserPartSlots.AdminDetailSidebar);
    }
}
