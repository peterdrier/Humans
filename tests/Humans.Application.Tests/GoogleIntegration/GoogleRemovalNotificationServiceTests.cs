using AwesomeAssertions;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.GoogleIntegration;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.GoogleIntegration;

/// <summary>
/// Unit tests for <see cref="GoogleRemovalNotificationService"/> — the
/// email-on-Google-removal flow introduced for issue peterdrier/Humans#639.
/// Covers Variant 1 vs Variant 2 selection, suppression cases, and the
/// orphan-address branch.
/// </summary>
public sealed class GoogleRemovalNotificationServiceTests
{
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IEmailService _emailService = Substitute.For<IEmailService>();
    private readonly GoogleRemovalNotificationService _service;

    public GoogleRemovalNotificationServiceTests()
    {
        _service = new GoogleRemovalNotificationService(
            _userEmailService,
            _userService,
            _emailService,
            NullLogger<GoogleRemovalNotificationService>.Instance);
    }

    [HumansFact]
    public async Task NotifyRemovalAsync_OrphanAddress_DoesNotSendEmail()
    {
        // No UserEmail row matches the address — orphan / deleted-user / self-unlink case.
        _userEmailService.GetUserIdByVerifiedEmailAsync("ghost@example.com", Arg.Any<CancellationToken>())
            .Returns((Guid?)null);

        await _service.NotifyRemovalAsync(
            "ghost@example.com",
            GoogleResourceType.Group,
            "Some Group",
            "some-group@nobodies.team",
            SyncRemovalReason.Reconciliation);

        await _emailService.DidNotReceive().SendGoogleGroupRemovalLossOfAccessAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _emailService.DidNotReceive().SendGoogleDriveRemovalLossOfAccessAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _emailService.DidNotReceive().SendGoogleAccessRemovalSecondaryCleanupAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task NotifyRemovalAsync_EmailRotation_StillSendsVariant2()
    {
        // Workspace identity rotated from old@ → new@. Caller passes
        // EmailRotation as advisory telemetry but the recipient still gets
        // a Variant 2 ("secondary cleanup") email confirming the rotation
        // (issue peterdrier/Humans#639 — suppression-on-rotation removed
        // because the user wants visibility into which address was tidied).
        var userId = Guid.NewGuid();
        var user = BuildUserWithEmails(
            userId,
            "Alice",
            "en",
            ("old@nobodies.team", verified: true, isGoogle: false),
            ("new@nobodies.team", verified: true, isGoogle: true));

        _userEmailService.GetUserIdByVerifiedEmailAsync("old@nobodies.team", Arg.Any<CancellationToken>())
            .Returns(userId);
        _userService.GetByIdsWithEmailsAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
            Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, User> { [userId] = user });
        _userService.GetUserInfosAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
            Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo> { [userId] = user.ToUserInfo(user.UserEmails.ToList()) }));

        await _service.NotifyRemovalAsync(
            "old@nobodies.team",
            GoogleResourceType.Group,
            "Some Group",
            "some-group@nobodies.team",
            SyncRemovalReason.EmailRotation);

        await _emailService.Received(1).SendGoogleAccessRemovalSecondaryCleanupAsync(
            "old@nobodies.team",
            "Alice",
            "new@nobodies.team",
            Arg.Any<string?>(),
            Arg.Any<CancellationToken>());
        await _emailService.DidNotReceive().SendGoogleGroupRemovalLossOfAccessAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task NotifyRemovalAsync_SecondaryCleanup_SendsVariant2()
    {
        // Variant 2 fixture: the removed address has IsGoogle=true, AND a
        // sibling row also has IsGoogle=true that is not being removed.
        // The selector picks the sibling as the surviving primary.
        var userId = Guid.NewGuid();
        var user = BuildUserWithEmails(
            userId,
            "Alice",
            "fr",
            ("old@nobodies.team", verified: true, isGoogle: true),
            ("new@nobodies.team", verified: true, isGoogle: true));

        _userEmailService.GetUserIdByVerifiedEmailAsync("old@nobodies.team", Arg.Any<CancellationToken>())
            .Returns(userId);
        _userService.GetByIdsWithEmailsAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
            Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, User> { [userId] = user });
        _userService.GetUserInfosAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
            Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo> { [userId] = user.ToUserInfo(user.UserEmails.ToList()) }));

        await _service.NotifyRemovalAsync(
            "old@nobodies.team",
            GoogleResourceType.Group,
            "My Group",
            "my-group@nobodies.team",
            SyncRemovalReason.Reconciliation);

        await _emailService.Received(1).SendGoogleAccessRemovalSecondaryCleanupAsync(
            "old@nobodies.team",
            "Alice",
            "new@nobodies.team",
            "fr",
            Arg.Any<CancellationToken>());

        // Variant 1 sub-templates must NOT be invoked.
        await _emailService.DidNotReceive().SendGoogleGroupRemovalLossOfAccessAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await _emailService.DidNotReceive().SendGoogleDriveRemovalLossOfAccessAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task NotifyRemovalAsync_LossOfAccess_Group_SendsVariant1Group()
    {
        var userId = Guid.NewGuid();
        var user = BuildUserWithEmails(
            userId,
            "Bob",
            "es",
            ("primary@nobodies.team", verified: true, isGoogle: true));

        _userEmailService.GetUserIdByVerifiedEmailAsync("primary@nobodies.team", Arg.Any<CancellationToken>())
            .Returns(userId);
        _userService.GetByIdsWithEmailsAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
            Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, User> { [userId] = user });
        _userService.GetUserInfosAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
            Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo> { [userId] = user.ToUserInfo(user.UserEmails.ToList()) }));

        await _service.NotifyRemovalAsync(
            "primary@nobodies.team",
            GoogleResourceType.Group,
            "Comms Team",
            "comms@nobodies.team",
            SyncRemovalReason.Reconciliation);

        await _emailService.Received(1).SendGoogleGroupRemovalLossOfAccessAsync(
            "primary@nobodies.team",
            "Bob",
            "Comms Team",
            "comms@nobodies.team",
            "es",
            Arg.Any<CancellationToken>());
        await _emailService.DidNotReceive().SendGoogleAccessRemovalSecondaryCleanupAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task NotifyRemovalAsync_LossOfAccess_Drive_SendsVariant1Drive()
    {
        var userId = Guid.NewGuid();
        var user = BuildUserWithEmails(
            userId,
            "Carol",
            "ca",
            ("only@nobodies.team", verified: true, isGoogle: true));

        _userEmailService.GetUserIdByVerifiedEmailAsync("only@nobodies.team", Arg.Any<CancellationToken>())
            .Returns(userId);
        _userService.GetByIdsWithEmailsAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
            Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, User> { [userId] = user });
        _userService.GetUserInfosAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
            Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo> { [userId] = user.ToUserInfo(user.UserEmails.ToList()) }));

        await _service.NotifyRemovalAsync(
            "only@nobodies.team",
            GoogleResourceType.DriveFolder,
            "Public Resources",
            "https://drive.google.com/drive/folders/abc",
            SyncRemovalReason.Reconciliation);

        await _emailService.Received(1).SendGoogleDriveRemovalLossOfAccessAsync(
            "only@nobodies.team",
            "Carol",
            "Public Resources",
            "ca",
            Arg.Any<CancellationToken>());
        await _emailService.DidNotReceive().SendGoogleGroupRemovalLossOfAccessAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task NotifyRemovalAsync_MissingResourceName_FallsBackToIdentifier()
    {
        var userId = Guid.NewGuid();
        var user = BuildUserWithEmails(
            userId,
            "Dee",
            "en",
            ("dee@nobodies.team", verified: true, isGoogle: true));

        _userEmailService.GetUserIdByVerifiedEmailAsync("dee@nobodies.team", Arg.Any<CancellationToken>())
            .Returns(userId);
        _userService.GetByIdsWithEmailsAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
            Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, User> { [userId] = user });
        _userService.GetUserInfosAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(userId)),
            Arg.Any<CancellationToken>())
            .Returns(new ValueTask<IReadOnlyDictionary<Guid, UserInfo>>(
                new Dictionary<Guid, UserInfo> { [userId] = user.ToUserInfo(user.UserEmails.ToList()) }));

        await _service.NotifyRemovalAsync(
            "dee@nobodies.team",
            GoogleResourceType.Group,
            resourceName: null,
            resourceIdentifier: "fallback@nobodies.team",
            SyncRemovalReason.Reconciliation);

        await _emailService.Received(1).SendGoogleGroupRemovalLossOfAccessAsync(
            "dee@nobodies.team",
            "Dee",
            "fallback@nobodies.team", // displayName falls back to identifier
            "fallback@nobodies.team",
            "en",
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task NotifyRemovalAsync_BlankRemovedEmail_NoOp()
    {
        await _service.NotifyRemovalAsync(
            string.Empty,
            GoogleResourceType.Group,
            "G",
            "g@nobodies.team",
            SyncRemovalReason.Reconciliation);

        await _userEmailService.DidNotReceiveWithAnyArgs()
            .GetUserIdByVerifiedEmailAsync(default!, Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Constructs a User with the given UserEmail rows. Used by the tests
    /// above to drive the variant selector.
    /// </summary>
    private static User BuildUserWithEmails(
        Guid userId,
        string displayName,
        string preferredLanguage,
        params (string Email, bool verified, bool isGoogle)[] emails)
    {
        var user = new User
        {
            Id = userId,
            DisplayName = displayName,
            UserName = $"user-{userId:N}",
            Email = emails.Length > 0 ? emails[0].Email : "fallback@example.com",
            PreferredLanguage = preferredLanguage
        };

        foreach (var (email, verified, isGoogle) in emails)
        {
            user.UserEmails.Add(new UserEmail
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Email = email,
                IsVerified = verified,
                IsGoogle = isGoogle
            });
        }

        return user;
    }
}
