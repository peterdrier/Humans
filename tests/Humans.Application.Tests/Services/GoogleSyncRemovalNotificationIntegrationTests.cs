using AwesomeAssertions;
using Humans.Application.Configuration;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.GoogleIntegration;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services;

/// <summary>
/// End-to-end test of the sync removal-notification flow (issue
/// peterdrier/Humans#639). Wires <see cref="GoogleWorkspaceSyncService"/>'s
/// gateway methods to a real <see cref="GoogleRemovalNotificationService"/>
/// and asserts the resulting <see cref="IEmailService"/> calls. The
/// integration boundary is <c>RemoveUserFromGroupAsync</c> (the spec's
/// "successful Google API delete" trigger). Drive-side coverage uses the
/// notification service unit tests; the sync service treats Drive removal
/// identically — same gateway, same notification call.
/// </summary>
public sealed class GoogleSyncRemovalNotificationIntegrationTests
{
    private readonly IGoogleGroupMembershipClient _groupMembership = Substitute.For<IGoogleGroupMembershipClient>();
    private readonly IGoogleGroupProvisioningClient _groupProvisioning = Substitute.For<IGoogleGroupProvisioningClient>();
    private readonly IGoogleDrivePermissionsClient _drivePermissions = Substitute.For<IGoogleDrivePermissionsClient>();
    private readonly IGoogleDirectoryClient _directory = Substitute.For<IGoogleDirectoryClient>();
    private readonly ITeamResourceGoogleClient _teamResourceClient = Substitute.For<ITeamResourceGoogleClient>();
    private readonly IGoogleResourceRepository _resourceRepository = Substitute.For<IGoogleResourceRepository>();
    private readonly IGoogleSyncOutboxRepository _googleSyncOutboxRepository = Substitute.For<IGoogleSyncOutboxRepository>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly IAuditLogService _auditLogService = Substitute.For<IAuditLogService>();
    private readonly ISyncSettingsService _syncSettingsService = Substitute.For<ISyncSettingsService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();

    private readonly GoogleWorkspaceSyncService _syncService;

    private static readonly Guid TestGroupResourceId = Guid.NewGuid();
    private const string TestGoogleId = "01abc";
    private const string TestGroupName = "QA Team";
    private const string TestGroupUrl = "https://groups.google.com/a/nobodies.team/g/qa-team";
    private const string TestGroupEmail = "qa-team@nobodies.team";

    public GoogleSyncRemovalNotificationIntegrationTests()
    {
        // AddAndRemove mode = removal gateway is enabled.
        _syncSettingsService
            .GetModeAsync(SyncServiceType.GoogleGroups, Arg.Any<CancellationToken>())
            .Returns(SyncMode.AddAndRemove);

        // Real GoogleRemovalNotificationService — the integration boundary
        // we are exercising. Its IUserEmailService / IUserService / IEmailService
        // dependencies are fakes.
        var notifications = new GoogleRemovalNotificationService(
            _userEmailService,
            _userService,
            _emailService,
            NullLogger<GoogleRemovalNotificationService>.Instance);

        var options = Options.Create(new GoogleWorkspaceOptions { Domain = "nobodies.team" });
        var clock = new FakeClock(Instant.FromUtc(2026, 5, 4, 12, 0));
        var serviceProvider = new ServiceCollection().BuildServiceProvider();

        _syncService = new GoogleWorkspaceSyncService(
            _groupMembership,
            _groupProvisioning,
            _drivePermissions,
            _directory,
            _teamResourceClient,
            _resourceRepository,
            _googleSyncOutboxRepository,
            _teamService,
            _userService,
            _userEmailService,
            _auditLogService,
            _syncSettingsService,
            notifications,
            options,
            clock,
            serviceProvider,
            NullLogger<GoogleWorkspaceSyncService>.Instance);
    }

    [HumansFact]
    public async Task RemoveUserFromGroup_StaleSecondaryEmail_EnqueuesOneVariant2_ZeroVariant1()
    {
        // ── Arrange: a Google Group with a stale secondary email "old@nobodies.team"
        // belonging to a user whose primary IsGoogle row is "new@nobodies.team".
        // Reconciliation removes the secondary; the user retains primary access.
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
        _userService.GetByIdsWithEmailsAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
            Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, User> { [userId] = user });

        // ── Act
        await _syncService.RemoveUserFromGroupAsync(TestGroupResourceId, removedEmail);

        // ── Assert: exactly one Variant 2 enqueue, zero Variant 1.
        await _emailService.Received(1).SendGoogleAccessRemovalSecondaryCleanupAsync(
            removedEmail,
            "Alice",
            primaryEmail,
            "es",
            Arg.Any<CancellationToken>());
        await _emailService.DidNotReceive().SendGoogleGroupRemovalLossOfAccessAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _emailService.DidNotReceive().SendGoogleDriveRemovalLossOfAccessAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task RemoveUserFromGroup_FailedGoogleDelete_DoesNotNotify()
    {
        // Spec: notify only on confirmed removal — a failed API call must not enqueue.
        const string removedEmail = "alice@nobodies.team";
        StageGroupResource();
        _groupMembership.ListMembershipsAsync(TestGoogleId, Arg.Any<CancellationToken>())
            .Returns(new GroupMembershipListResult(
                Memberships: new[] { new GroupMembership(removedEmail, "groups/01abc/memberships/m1") },
                Error: null));
        _groupMembership.DeleteMembershipAsync("groups/01abc/memberships/m1", Arg.Any<CancellationToken>())
            .Returns(new GoogleClientError(StatusCode: 500, RawMessage: "boom"));

        await _syncService.RemoveUserFromGroupAsync(TestGroupResourceId, removedEmail);

        await _emailService.DidNotReceive().SendGoogleGroupRemovalLossOfAccessAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _emailService.DidNotReceive().SendGoogleAccessRemovalSecondaryCleanupAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    private void StageGroupResource()
    {
        _resourceRepository.GetByIdAsync(TestGroupResourceId, Arg.Any<CancellationToken>())
            .Returns(new GoogleResource
            {
                Id = TestGroupResourceId,
                ResourceType = GoogleResourceType.Group,
                GoogleId = TestGoogleId,
                Name = TestGroupName,
                Url = TestGroupUrl,
                IsActive = true
            });
    }

    private void StageGoogleApiSuccess(string memberEmail)
    {
        _groupMembership.ListMembershipsAsync(TestGoogleId, Arg.Any<CancellationToken>())
            .Returns(new GroupMembershipListResult(
                Memberships: new[] { new GroupMembership(memberEmail, "groups/01abc/memberships/m1") },
                Error: null));
        _groupMembership.DeleteMembershipAsync("groups/01abc/memberships/m1", Arg.Any<CancellationToken>())
            .Returns((GoogleClientError?)null);
    }
}
