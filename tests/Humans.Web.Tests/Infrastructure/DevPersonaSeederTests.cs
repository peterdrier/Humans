using AwesomeAssertions;
using Humans.Application;
using Humans.Application.Configuration;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Consent;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.HumanLifecycle;
using Humans.Application.Interfaces.Onboarding;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Web.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Web.Tests.Infrastructure;

/// <summary>
/// Covers the #867 persona repair: dev personas hold governance roles but never signed
/// the required legal documents, so the nightly SuspendNonCompliantMembersJob suspended
/// them once a document's grace period lapsed, and the create-only seeder could never
/// bring them back. EnsureActiveAsync must submit missing consents through the canonical
/// consent path and lift a consent suspension, on every dev sign-in, idempotently.
/// </summary>
public class DevPersonaSeederTests
{
    private readonly UserManager<User> _userManager = Substitute.For<UserManager<User>>(
        Substitute.For<IUserStore<User>>(), null, null, null, null, null, null, null, null);
    private readonly IProfileEditorService _profileEditor = Substitute.For<IProfileEditorService>();
    private readonly IUserEmailService _userEmails = Substitute.For<IUserEmailService>();
    private readonly IContactFieldService _contactFields = Substitute.For<IContactFieldService>();
    private readonly IRoleAssignmentService _roleAssignments = Substitute.For<IRoleAssignmentService>();
    private readonly IUserInfoInvalidator _userInfoInvalidator = Substitute.For<IUserInfoInvalidator>();
    private readonly ITeamService _teams = Substitute.For<ITeamService>();
    private readonly ISystemTeamSync _systemTeamSync = Substitute.For<ISystemTeamSync>();
    private readonly IUserService _users = Substitute.For<IUserService>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private readonly ICampService _camps = Substitute.For<ICampService>();
    private readonly ICampRoleService _campRoles = Substitute.For<ICampRoleService>();
    private readonly IConsentService _consents = Substitute.For<IConsentService>();
    private readonly IMembershipCalculatorRead _membershipCalculator = Substitute.For<IMembershipCalculatorRead>();
    private readonly IHumanLifecycleService _humanLifecycle = Substitute.For<IHumanLifecycleService>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 7, 13, 12, 0));
    private readonly IMemoryCache _cache = new MemoryCache(new MemoryCacheOptions());

    private DevPersonaSeeder BuildSut() => new(
        _userManager,
        _profileEditor,
        _userEmails,
        _contactFields,
        _roleAssignments,
        _userInfoInvalidator,
        _teams,
        _systemTeamSync,
        _users,
        _audit,
        _camps,
        _campRoles,
        _consents,
        _membershipCalculator,
        _humanLifecycle,
        _clock,
        _cache,
        Options.Create(new CityPlanningOptions()),
        NullLogger<DevPersonaSeeder>.Instance);

    private static UserInfo NamedUserInfo(Guid userId, UserState state) =>
        UserInfo.Create(
            new User { Id = userId, DisplayName = "Dev Board", State = state },
            [], [], [],
            profile: new Profile
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                BurnerName = "Dev Board",
                FirstName = "Dev",
                LastName = "Board",
                State = state == UserState.Suspended ? ProfileState.Suspended : ProfileState.Active,
                IsSuspended = state == UserState.Suspended,
                IsApproved = true,
            },
            [], [], [], []);

    private static MembershipSnapshot Snapshot(params Guid[] missingVersionIds) => new(
        MembershipStatus.Active,
        IsVolunteerMember: true,
        RequiredConsentCount: missingVersionIds.Length,
        PendingConsentCount: missingVersionIds.Length,
        MissingConsentVersionIds: missingVersionIds);

    [HumansFact]
    public async Task EnsureActiveAsync_MissingConsents_SubmitsEachThroughConsentServiceAndRestores()
    {
        var userId = Guid.NewGuid();
        var v1 = Guid.NewGuid();
        var v2 = Guid.NewGuid();
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(NamedUserInfo(userId, UserState.Suspended)));
        _membershipCalculator.GetMembershipSnapshotAsync(userId, Arg.Any<CancellationToken>())
            .Returns(Snapshot(v1, v2));
        _consents.SubmitConsentAsync(userId, Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConsentSubmitResult(true));
        _humanLifecycle.RestoreConsentSuspensionAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(true));

        await BuildSut().EnsureActiveAsync(userId);

        await _consents.Received(1).SubmitConsentAsync(
            userId, v1, true, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _consents.Received(1).SubmitConsentAsync(
            userId, v2, true, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _humanLifecycle.Received(1).RestoreConsentSuspensionAsync(userId, Arg.Any<CancellationToken>());
        // Volunteers admission is name + consents and SubmitConsentAsync does not provision
        // membership — the repair must re-sync or the persona stays out until the nightly job.
        await _systemTeamSync.Received(1).SyncMembershipForUserAsync(
            userId, SystemTeamType.Volunteers, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task EnsureActiveAsync_NothingMissing_StillLiftsConsentSuspension()
    {
        // The required-document set can shrink after a suspension: nothing is missing
        // anymore, but the profile is still Suspended. The repair must not depend on a
        // consent submit to trigger the restore.
        var userId = Guid.NewGuid();
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(NamedUserInfo(userId, UserState.Suspended)));
        _membershipCalculator.GetMembershipSnapshotAsync(userId, Arg.Any<CancellationToken>())
            .Returns(Snapshot());
        _humanLifecycle.RestoreConsentSuspensionAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(true));

        await BuildSut().EnsureActiveAsync(userId);

        await _consents.DidNotReceive().SubmitConsentAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _humanLifecycle.Received(1).RestoreConsentSuspensionAsync(userId, Arg.Any<CancellationToken>());
        // Was suspended → the (nightly) suspension flow removed Volunteers membership; re-sync.
        await _systemTeamSync.Received(1).SyncMembershipForUserAsync(
            userId, SystemTeamType.Volunteers, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task EnsureActiveAsync_AlreadyActiveNothingMissing_DoesNotResyncVolunteers()
    {
        // The steady-state login (persona healthy) must not churn team sync on every hit.
        var userId = Guid.NewGuid();
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(NamedUserInfo(userId, UserState.Active)));
        _membershipCalculator.GetMembershipSnapshotAsync(userId, Arg.Any<CancellationToken>())
            .Returns(Snapshot());
        _humanLifecycle.RestoreConsentSuspensionAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(true));

        await BuildSut().EnsureActiveAsync(userId);

        await _systemTeamSync.DidNotReceive().SyncMembershipForUserAsync(
            Arg.Any<Guid>(), Arg.Any<SystemTeamType>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task EnsureActiveAsync_RestoreFails_DoesNotResyncVolunteers()
    {
        // A still-suspended user is ineligible, and a single-user Volunteers sync REMOVES
        // ineligible members — never sync when the restore did not succeed.
        var userId = Guid.NewGuid();
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(NamedUserInfo(userId, UserState.Suspended)));
        _membershipCalculator.GetMembershipSnapshotAsync(userId, Arg.Any<CancellationToken>())
            .Returns(Snapshot());
        _humanLifecycle.RestoreConsentSuspensionAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(false, "NotFound"));

        await BuildSut().EnsureActiveAsync(userId);

        await _systemTeamSync.DidNotReceive().SyncMembershipForUserAsync(
            Arg.Any<Guid>(), Arg.Any<SystemTeamType>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task EnsureActiveAsync_ProfilelessOrBlankNames_SkipsRepair()
    {
        // Guest personas have no profile; the No-Name persona has blanked legal names.
        // Neither can consent (Stub gate in SubmitConsentAsync) and neither holds a
        // governance role, so the suspend job never targets them — skip entirely.
        var profileless = Guid.NewGuid();
        _users.GetUserInfoAsync(profileless, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfo.Create(
                new User { Id = profileless, DisplayName = "Dev Guest" },
                [], [], [], profile: null, [], [], [], [])));

        await BuildSut().EnsureActiveAsync(profileless);

        await _membershipCalculator.DidNotReceive().GetMembershipSnapshotAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _consents.DidNotReceive().SubmitConsentAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _humanLifecycle.DidNotReceive().RestoreConsentSuspensionAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task EnsurePersonaAsync_ExistingPersona_RunsActiveRepair()
    {
        // The pre-#867 seeder early-returned for existing personas, so a persona that
        // drifted non-Active could never recover. The existing-persona path must repair.
        var userId = DevPersonaSeeder.PersonaGuid("board");
        _userManager.FindByIdAsync(userId.ToString())
            .Returns(new User { Id = userId, DisplayName = "Dev Board" });
        _users.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(NamedUserInfo(userId, UserState.Suspended)));
        _membershipCalculator.GetMembershipSnapshotAsync(userId, Arg.Any<CancellationToken>())
            .Returns(Snapshot(Guid.NewGuid()));
        _consents.SubmitConsentAsync(userId, Arg.Any<Guid>(), Arg.Any<bool>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new ConsentSubmitResult(true));
        _humanLifecycle.RestoreConsentSuspensionAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(true));

        var resolved = await BuildSut().EnsurePersonaAsync("board", "Board", userId);

        resolved.Should().Be(userId);
        await _userManager.DidNotReceive().CreateAsync(Arg.Any<User>());
        await _consents.Received(1).SubmitConsentAsync(
            userId, Arg.Any<Guid>(), true, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _humanLifecycle.Received(1).RestoreConsentSuspensionAsync(userId, Arg.Any<CancellationToken>());
    }
}
