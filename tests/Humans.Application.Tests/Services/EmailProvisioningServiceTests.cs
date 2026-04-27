using AwesomeAssertions;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Email;
using Humans.Application.Interfaces.GoogleIntegration;
using Humans.Application.Interfaces.Notifications;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.GoogleIntegration;
using Humans.Domain.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services;

public class EmailProvisioningServiceTests
{
    [HumansTheory]
    [InlineData("mueller", "mueller")]
    [InlineData("müller", "mueller")]
    [InlineData("Müller", "mueller")]
    [InlineData("schön", "schoen")]
    [InlineData("Ärzte", "aerzte")]
    [InlineData("straße", "strasse")]
    public void SanitizeEmailPrefix_TransliteratesGermanCharacters(string input, string expected)
    {
        EmailProvisioningService.SanitizeEmailPrefix(input).Should().Be(expected);
    }

    [HumansTheory]
    [InlineData("garcía", "garcia")]
    [InlineData("café", "cafe")]
    [InlineData("naïve", "naive")]
    [InlineData("résumé", "resume")]
    [InlineData("señor", "senor")]
    public void SanitizeEmailPrefix_StripsAccentsViaNfdDecomposition(string input, string expected)
    {
        EmailProvisioningService.SanitizeEmailPrefix(input).Should().Be(expected);
    }

    [HumansTheory]
    [InlineData("müller.garcía", "mueller.garcia")]
    [InlineData("Böhm-López", "boehm-lopez")]
    public void SanitizeEmailPrefix_HandlesMixedGermanAndAccented(string input, string expected)
    {
        EmailProvisioningService.SanitizeEmailPrefix(input).Should().Be(expected);
    }

    [HumansFact]
    public void SanitizeEmailPrefix_ReturnsNullForNonTransliterableCharacters()
    {
        EmailProvisioningService.SanitizeEmailPrefix("田中").Should().BeNull();
    }

    [HumansFact]
    public void SanitizeEmailPrefix_ReturnsEmptyForWhitespaceOnly()
    {
        EmailProvisioningService.SanitizeEmailPrefix("   ").Should().BeEmpty();
    }

    [HumansFact]
    public void SanitizeEmailPrefix_ReturnsNullForEmbeddedSpaces()
    {
        EmailProvisioningService.SanitizeEmailPrefix("jo hn").Should().BeNull();
    }

    [HumansFact]
    public void SanitizeEmailPrefix_TrimsLeadingAndTrailingWhitespace()
    {
        EmailProvisioningService.SanitizeEmailPrefix("  alice  ").Should().Be("alice");
    }

    [HumansFact]
    public void SanitizeEmailPrefix_ConvertsToLowerCase()
    {
        EmailProvisioningService.SanitizeEmailPrefix("Alice").Should().Be("alice");
    }

    [HumansFact]
    public void SanitizeEmailPrefix_ReturnsNullForEmbeddedControlCharacters()
    {
        EmailProvisioningService.SanitizeEmailPrefix("te\tst").Should().BeNull();
    }

    // --- ProvisionNobodiesEmailAsync: cross-user conflict checks ---
    // These tests verify that provisioning rejects prefixes already in use
    // by another human in our system BEFORE calling Workspace or writing DB.
    // All DB state is mocked at the IUserService / IUserEmailService / ITeamService
    // boundary — the Application-layer service no longer touches DbContext.

    private sealed record ProvisioningFixture(
        EmailProvisioningService Service,
        IUserService UserService,
        IProfileService ProfileService,
        IGoogleWorkspaceUserService WorkspaceUserService,
        IUserEmailService UserEmailService,
        ITeamService TeamService,
        IEmailService EmailService,
        INotificationService NotificationService,
        IAuditLogService AuditLogService);

    private static ProvisioningFixture BuildFixture()
    {
        var userService = Substitute.For<IUserService>();
        var profileService = Substitute.For<IProfileService>();
        var workspace = Substitute.For<IGoogleWorkspaceUserService>();
        var userEmail = Substitute.For<IUserEmailService>();
        var teamService = Substitute.For<ITeamService>();
        var email = Substitute.For<IEmailService>();
        var notify = Substitute.For<INotificationService>();
        var audit = Substitute.For<IAuditLogService>();

        var service = new EmailProvisioningService(
            userService, profileService, workspace, userEmail, teamService,
            email, notify, audit,
            NullLogger<EmailProvisioningService>.Instance);

        return new ProvisioningFixture(
            service, userService, profileService, workspace, userEmail,
            teamService, email, notify, audit);
    }

    private static void StubTargetUser(ProvisioningFixture f, Guid userId, string? oauthEmail = "target@example.com")
    {
        f.UserService.GetByIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new User
            {
                Id = userId,
                Email = oauthEmail,
                DisplayName = "Target",
            });
        f.ProfileService.GetProfileAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new Profile { FirstName = "Target", LastName = "Two" });
    }

    [HumansFact]
    public async Task ProvisionNobodiesEmailAsync_RejectsWhenEmailBelongsToAnotherUserEmail()
    {
        var f = BuildFixture();

        var ownerId = Guid.NewGuid();
        var targetId = Guid.NewGuid();

        StubTargetUser(f, targetId);
        f.UserEmailService.GetOtherUserIdHavingEmailAsync(
                "alice@nobodies.team", targetId, Arg.Any<CancellationToken>())
            .Returns(ownerId);

        var result = await f.Service.ProvisionNobodiesEmailAsync(targetId, "alice", targetId);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("already in use by another human");

        // No Workspace call, no email link
        await f.WorkspaceUserService.DidNotReceive().GetAccountAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await f.WorkspaceUserService.DidNotReceive().ProvisionAccountAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await f.UserEmailService.DidNotReceive().AddVerifiedEmailAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ProvisionNobodiesEmailAsync_RejectsWhenEmailBelongsToAnotherGoogleEmail()
    {
        var f = BuildFixture();

        var ownerId = Guid.NewGuid();
        var targetId = Guid.NewGuid();

        StubTargetUser(f, targetId);
        f.UserService.GetOtherUserIdHavingGoogleEmailAsync(
                "alice@nobodies.team", targetId, Arg.Any<CancellationToken>())
            .Returns(ownerId);

        var result = await f.Service.ProvisionNobodiesEmailAsync(targetId, "alice", targetId);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("already in use by another human");

        await f.WorkspaceUserService.DidNotReceive().GetAccountAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await f.WorkspaceUserService.DidNotReceive().ProvisionAccountAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await f.UserEmailService.DidNotReceive().AddVerifiedEmailAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ProvisionNobodiesEmailAsync_RejectsWhenPrefixCollidesWithTeamGoogleGroup()
    {
        var f = BuildFixture();

        var userId = Guid.NewGuid();
        StubTargetUser(f, userId);
        f.TeamService.GetTeamNameByGoogleGroupPrefixAsync(
                "comms", Arg.Any<CancellationToken>())
            .Returns("Communications");

        var result = await f.Service.ProvisionNobodiesEmailAsync(userId, "comms", userId);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Google Group");
        result.ErrorMessage.Should().Contain("Communications");

        // No Workspace call, no email link
        await f.WorkspaceUserService.DidNotReceive().GetAccountAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await f.WorkspaceUserService.DidNotReceive().ProvisionAccountAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await f.UserEmailService.DidNotReceive().AddVerifiedEmailAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task ProvisionNobodiesEmailAsync_AllowsWhenPrefixIsFree()
    {
        var f = BuildFixture();

        var userId = Guid.NewGuid();
        StubTargetUser(f, userId, oauthEmail: "person@example.com");
        f.ProfileService.GetProfileAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new Profile { FirstName = "Person", LastName = "Test" });
        f.UserEmailService.GetUserEmailsAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmailEditDto>());

        f.WorkspaceUserService.GetAccountAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((WorkspaceUserAccount?)null);
        f.WorkspaceUserService.ProvisionAccountAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new WorkspaceUserAccount(
                "bob@nobodies.team", "Person", "Test", false,
                DateTime.UtcNow, null, IsEnrolledIn2Sv: false));

        var result = await f.Service.ProvisionNobodiesEmailAsync(userId, "bob", userId);

        result.Success.Should().BeTrue();
        result.FullEmail.Should().Be("bob@nobodies.team");

        await f.WorkspaceUserService.Received(1).ProvisionAccountAsync(
            "bob@nobodies.team", "Person", "Test",
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await f.UserEmailService.Received(1).AddVerifiedEmailAsync(
            userId, "bob@nobodies.team", Arg.Any<CancellationToken>());
    }
}
