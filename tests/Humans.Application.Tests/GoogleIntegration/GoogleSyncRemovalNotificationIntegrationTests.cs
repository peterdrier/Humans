using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Configuration;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.GoogleIntegration;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.GoogleIntegration;

/// <summary>
/// End-to-end test of the sync removal-notification flow (issue
/// peterdrier/Humans#639). Wires <see cref="GoogleGroupSyncService"/> to a
/// real <see cref="GoogleRemovalNotificationService"/> and asserts the
/// resulting <see cref="IEmailService"/> calls. The integration boundary is
/// now a confirmed Google Group membership delete inside the group
/// orchestrator.
/// </summary>
public sealed class GoogleSyncRemovalNotificationIntegrationTests
{
    private readonly IGoogleGroupMembershipClient _groupMembership = Substitute.For<IGoogleGroupMembershipClient>();
    private readonly IGoogleGroupProvisioningClient _groupProvisioning = Substitute.For<IGoogleGroupProvisioningClient>();
    private readonly ITeamResourceGoogleClient _teamResourceClient = Substitute.For<ITeamResourceGoogleClient>();
    private readonly ITeamResourceService _teamResourceService = Substitute.For<ITeamResourceService>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly ISyncSettingsService _syncSettingsService = Substitute.For<ISyncSettingsService>();
    private readonly IAuditLogService _auditLogService = Substitute.For<IAuditLogService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly IEmailMessageFactory _emailMessages = Substitute.For<IEmailMessageFactory>();
    private readonly RecordingGoogleGroupSyncScheduler _syncScheduler = new();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 5, 4, 12, 0));

    private readonly GoogleGroupSyncService _syncService;

    private static readonly Guid TestTeamId = Guid.NewGuid();
    private static readonly Guid TestGroupResourceId = Guid.NewGuid();
    private const string TestGoogleId = "01abc";
    private const string TestGroupName = "QA Team";
    private const string TestGroupUrl = "https://groups.google.com/a/nobodies.team/g/qa-team";
    private const string TestGroupEmail = "qa-team@nobodies.team";

    public GoogleSyncRemovalNotificationIntegrationTests()
    {
        _syncSettingsService
            .GetModeAsync(SyncServiceType.GoogleGroups, Arg.Any<CancellationToken>())
            .Returns(SyncMode.AddAndRemove);
        _teamResourceClient.GetServiceAccountEmailAsync(Arg.Any<CancellationToken>())
            .Returns("service-account@nobodies.team");

        var notifications = new GoogleRemovalNotificationService(
            _userEmailService,
            _userService,
            _emailService,
            _emailMessages,
            NullLogger<GoogleRemovalNotificationService>.Instance);

        _syncService = new GoogleGroupSyncService(
            [new StaticSource(TestGroupEmail)],
            _groupMembership,
            _groupProvisioning,
            _teamResourceClient,
            _teamResourceService,
            _teamService,
            _userService,
            _userEmailService,
            _syncSettingsService,
            _auditLogService,
            notifications,
            _syncScheduler,
            Options.Create(new GoogleWorkspaceOptions()),
            _clock,
            NullLogger<GoogleGroupSyncService>.Instance);
    }

    [HumansFact]
    public async Task ReconcileOneAsync_StaleSecondaryEmail_EnqueuesOneVariant2_ZeroVariant1()
    {
        const string removedEmail = "old@nobodies.team";
        const string primaryEmail = "new@nobodies.team";

        StageGroupResource();
        StageGoogleApiSuccess(removedEmail);

        var userId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            DisplayName = "Alice",
            UserName = $"user-{userId:N}",
            Email = primaryEmail,
            PreferredLanguage = "es"
        };
        user.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = removedEmail,
            IsVerified = true,
            IsGoogle = true
        });
        user.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = primaryEmail,
            IsVerified = true,
            IsGoogle = true
        });

        _userEmailService.GetUserIdByVerifiedEmailAsync(removedEmail, Arg.Any<CancellationToken>())
            .Returns(userId);
        _userService.GetUserInfosAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
            Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo> { [userId] = user.ToUserInfo(user.UserEmails.ToList()) }));

        await _syncService.ReconcileOneAsync(TestGroupEmail, SyncAction.Execute);

        _emailMessages.Received(1).GoogleAccessRemovalSecondaryCleanup(
            removedEmail,
            "Alice",
            primaryEmail,
            "es");
        _emailMessages.DidNotReceive().GoogleGroupRemovalLossOfAccess(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>());
        _emailMessages.DidNotReceive().GoogleDriveRemovalLossOfAccess(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>());
    }

    [HumansFact]
    public async Task ReconcileOneAsync_FailedGoogleDelete_DoesNotNotify()
    {
        const string removedEmail = "alice@nobodies.team";
        StageGroupResource();
        _groupProvisioning.LookupGroupIdAsync(TestGroupEmail, Arg.Any<CancellationToken>())
            .Returns(new GroupLookupIdResult(TestGoogleId, null));
        _groupMembership.ListMembershipsAsync(TestGoogleId, Arg.Any<CancellationToken>())
            .Returns(new GroupMembershipListResult(
                [new GroupMembership(removedEmail, "groups/01abc/memberships/m1")],
                Error: null));
        _groupMembership.DeleteMembershipAsync("groups/01abc/memberships/m1", Arg.Any<CancellationToken>())
            .Returns(new GoogleClientError(StatusCode: 500, RawMessage: "boom"));

        await _syncService.ReconcileOneAsync(TestGroupEmail, SyncAction.Execute);

        _emailMessages.DidNotReceive().GoogleGroupRemovalLossOfAccess(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>());
        _emailMessages.DidNotReceive().GoogleAccessRemovalSecondaryCleanup(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>());
    }

    private void StageGroupResource()
    {
        _teamResourceService.GetActiveResourceCountsByTeamAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, int> { [TestTeamId] = 1 });
        _teamResourceService.GetResourcesByTeamIdsAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(TestTeamId)),
                Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, IReadOnlyList<GoogleResourceSnapshot>>
            {
                [TestTeamId] =
                [
                    new GoogleResourceSnapshot(
                        TestGroupResourceId,
                        TestTeamId,
                        TestGoogleId,
                        TestGroupName,
                        GoogleResourceType.Group,
                        TestGroupUrl,
                        IsActive: true)
                ]
            });
        _teamService.GetTeamByIdAsync(TestTeamId, Arg.Any<CancellationToken>())
            .Returns(new Team
            {
                Id = TestTeamId,
                Name = TestGroupName,
                Slug = "qa-team",
                GoogleGroupPrefix = "qa-team",
                CreatedAt = _clock.GetCurrentInstant(),
                UpdatedAt = _clock.GetCurrentInstant()
            });
    }

    private void StageGoogleApiSuccess(string memberEmail)
    {
        _groupProvisioning.LookupGroupIdAsync(TestGroupEmail, Arg.Any<CancellationToken>())
            .Returns(new GroupLookupIdResult(TestGoogleId, null));
        _groupMembership.ListMembershipsAsync(TestGoogleId, Arg.Any<CancellationToken>())
            .Returns(new GroupMembershipListResult(
                [new GroupMembership(memberEmail, "groups/01abc/memberships/m1")],
                Error: null));
        _groupMembership.DeleteMembershipAsync("groups/01abc/memberships/m1", Arg.Any<CancellationToken>())
            .Returns((GoogleClientError?)null);
    }

    private sealed class StaticSource(string key) : IGoogleGroupMembershipSource
    {
        public Task<Dictionary<string, Guid[]>> GetExpectedAsync(
            string? groupKey = null,
            CancellationToken ct = default)
        {
            if (groupKey is not null && !string.Equals(groupKey, key, StringComparison.OrdinalIgnoreCase))
                return Task.FromResult(new Dictionary<string, Guid[]>(StringComparer.OrdinalIgnoreCase));

            return Task.FromResult(new Dictionary<string, Guid[]>(StringComparer.OrdinalIgnoreCase)
            {
                [key] = []
            });
        }
    }

    private sealed class RecordingGoogleGroupSyncScheduler : IGoogleGroupSyncScheduler
    {
        public void Enqueue(string groupKey)
        {
        }

        public void Schedule(string groupKey, TimeSpan delay, int retryAttempt)
        {
        }
    }
}
