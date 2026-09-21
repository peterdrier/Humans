using Humans.GoogleIntegration.Contracts;
using Humans.Email.Contracts;
using Humans.Users.Contracts;
using Humans.GoogleIntegration.Services;
using Humans.GoogleIntegration.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Humans.GoogleIntegration.Tests;

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
    private readonly GoogleIntegrationEmails _emailMessages = TestGoogleIntegrationEmails.Create();
    private readonly GoogleRemovalNotificationService _service;

    public GoogleRemovalNotificationServiceTests()
    {
        _service = new GoogleRemovalNotificationService(
            _userEmailService,
            _userService,
            _emailService,
            _emailMessages,
            NullLogger<GoogleRemovalNotificationService>.Instance);
    }

    [HumansFact]
    public async Task NotifyRemovalAsync_OrphanAddress_DoesNotSendEmail()
    {
        // No UserEmail row matches the address — orphan / deleted-user / self-unlink case.
        _userEmailService.FindByAddressAsync("ghost@example.com", false, true, Arg.Any<CancellationToken>())
            .Returns([]);

        await _service.NotifyRemovalAsync(
            "ghost@example.com",
            GoogleResourceType.Group,
            "Some Group",
            "some-group@nobodies.team",
            SyncRemovalReason.Reconciliation, Xunit.TestContext.Current.CancellationToken);

        // The builder is sealed with no interface, so the sent message is the assertion surface.
        await _emailService.DidNotReceiveWithAnyArgs().SendAsync(default!, default);
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

        _userEmailService.FindByAddressAsync("old@nobodies.team", false, true, Arg.Any<CancellationToken>())
            .Returns([UserEmailFixtures.Row(userId, "old@nobodies.team")]);
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
            SyncRemovalReason.EmailRotation, Xunit.TestContext.Current.CancellationToken);

        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.TemplateName == "google_access_removal_secondary_cleanup"
                && m.RecipientEmail == "old@nobodies.team" && m.RecipientName == "Alice"
                && m.HtmlBody.Contains("new@nobodies.team", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await _emailService.DidNotReceive().SendAsync(
            Arg.Is<EmailMessage>(m => m.TemplateName == "google_group_removal_loss_of_access"),
            Arg.Any<CancellationToken>());
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

        _userEmailService.FindByAddressAsync("old@nobodies.team", false, true, Arg.Any<CancellationToken>())
            .Returns([UserEmailFixtures.Row(userId, "old@nobodies.team")]);
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
            SyncRemovalReason.Reconciliation, Xunit.TestContext.Current.CancellationToken);

        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.TemplateName == "google_access_removal_secondary_cleanup"
                && m.RecipientEmail == "old@nobodies.team" && m.RecipientName == "Alice"
                && m.HtmlBody.Contains("new@nobodies.team", StringComparison.Ordinal)
                && m.Subject.EndsWith("#fr", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());

        // Variant 1 sub-templates must NOT be sent.
        await _emailService.DidNotReceive().SendAsync(
            Arg.Is<EmailMessage>(m => m.TemplateName == "google_group_removal_loss_of_access"
                || m.TemplateName == "google_drive_removal_loss_of_access"),
            Arg.Any<CancellationToken>());
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

        _userEmailService.FindByAddressAsync("primary@nobodies.team", false, true, Arg.Any<CancellationToken>())
            .Returns([UserEmailFixtures.Row(userId, "primary@nobodies.team")]);
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
            SyncRemovalReason.Reconciliation, Xunit.TestContext.Current.CancellationToken);

        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.TemplateName == "google_group_removal_loss_of_access"
                && m.RecipientEmail == "primary@nobodies.team" && m.RecipientName == "Bob"
                && m.HtmlBody.Contains("Comms Team", StringComparison.Ordinal)
                && m.HtmlBody.Contains("comms@nobodies.team", StringComparison.Ordinal)
                && m.Subject.EndsWith("#es", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await _emailService.DidNotReceive().SendAsync(
            Arg.Is<EmailMessage>(m => m.TemplateName == "google_access_removal_secondary_cleanup"),
            Arg.Any<CancellationToken>());
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

        _userEmailService.FindByAddressAsync("only@nobodies.team", false, true, Arg.Any<CancellationToken>())
            .Returns([UserEmailFixtures.Row(userId, "only@nobodies.team")]);
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
            SyncRemovalReason.Reconciliation, Xunit.TestContext.Current.CancellationToken);

        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.TemplateName == "google_drive_removal_loss_of_access"
                && m.RecipientEmail == "only@nobodies.team" && m.RecipientName == "Carol"
                && m.HtmlBody.Contains("Public Resources", StringComparison.Ordinal)
                && m.Subject.EndsWith("#ca", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());
        await _emailService.DidNotReceive().SendAsync(
            Arg.Is<EmailMessage>(m => m.TemplateName == "google_group_removal_loss_of_access"),
            Arg.Any<CancellationToken>());
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

        _userEmailService.FindByAddressAsync("dee@nobodies.team", false, true, Arg.Any<CancellationToken>())
            .Returns([UserEmailFixtures.Row(userId, "dee@nobodies.team")]);
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
            SyncRemovalReason.Reconciliation, cancellationToken: Xunit.TestContext.Current.CancellationToken);

        await _emailService.Received(1).SendAsync(
            Arg.Is<EmailMessage>(m => m.TemplateName == "google_group_removal_loss_of_access"
                && m.RecipientEmail == "dee@nobodies.team" && m.RecipientName == "Dee"
                // displayName falls back to the identifier, so it appears twice in the body
                && m.HtmlBody.Contains("<p>fallback@nobodies.team</p><p>fallback@nobodies.team</p>", StringComparison.Ordinal)
                && m.Subject.EndsWith("#en", StringComparison.Ordinal)),
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
            SyncRemovalReason.Reconciliation, Xunit.TestContext.Current.CancellationToken);

        await _userEmailService.DidNotReceiveWithAnyArgs()
            .FindByAddressAsync(null!, false, false, Arg.Any<CancellationToken>());
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
            BurnerName = displayName,
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
