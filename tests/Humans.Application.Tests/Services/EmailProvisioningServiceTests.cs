using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services;

public class EmailProvisioningServiceTests
{
    [Theory]
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

    [Theory]
    [InlineData("garcía", "garcia")]
    [InlineData("café", "cafe")]
    [InlineData("naïve", "naive")]
    [InlineData("résumé", "resume")]
    [InlineData("señor", "senor")]
    public void SanitizeEmailPrefix_StripsAccentsViaNfdDecomposition(string input, string expected)
    {
        EmailProvisioningService.SanitizeEmailPrefix(input).Should().Be(expected);
    }

    [Theory]
    [InlineData("müller.garcía", "mueller.garcia")]
    [InlineData("Böhm-López", "boehm-lopez")]
    public void SanitizeEmailPrefix_HandlesMixedGermanAndAccented(string input, string expected)
    {
        EmailProvisioningService.SanitizeEmailPrefix(input).Should().Be(expected);
    }

    [Fact]
    public void SanitizeEmailPrefix_ReturnsNullForNonTransliterableCharacters()
    {
        EmailProvisioningService.SanitizeEmailPrefix("田中").Should().BeNull();
    }

    [Fact]
    public void SanitizeEmailPrefix_ReturnsEmptyForWhitespaceOnly()
    {
        EmailProvisioningService.SanitizeEmailPrefix("   ").Should().BeEmpty();
    }

    [Fact]
    public void SanitizeEmailPrefix_ReturnsNullForEmbeddedSpaces()
    {
        EmailProvisioningService.SanitizeEmailPrefix("jo hn").Should().BeNull();
    }

    [Fact]
    public void SanitizeEmailPrefix_TrimsLeadingAndTrailingWhitespace()
    {
        EmailProvisioningService.SanitizeEmailPrefix("  alice  ").Should().Be("alice");
    }

    [Fact]
    public void SanitizeEmailPrefix_ConvertsToLowerCase()
    {
        EmailProvisioningService.SanitizeEmailPrefix("Alice").Should().Be("alice");
    }

    [Fact]
    public void SanitizeEmailPrefix_ReturnsNullForEmbeddedControlCharacters()
    {
        EmailProvisioningService.SanitizeEmailPrefix("te\tst").Should().BeNull();
    }

    // --- ProvisionNobodiesEmailAsync: DB conflict checks ---
    // These tests verify that provisioning rejects prefixes already in use
    // by another human in our system BEFORE calling Workspace or writing DB.

    private sealed record ProvisioningFixture(
        EmailProvisioningService Service,
        HumansDbContext DbContext,
        IGoogleWorkspaceUserService WorkspaceUserService,
        IUserEmailService UserEmailService,
        IEmailService EmailService,
        INotificationService NotificationService,
        IAuditLogService AuditLogService);

    private static ProvisioningFixture BuildFixture()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        var db = new HumansDbContext(options);

        var store = Substitute.For<IUserStore<User>>();
        var userManager = Substitute.For<UserManager<User>>(
            store, null, null, null, null, null, null, null, null);

        var workspace = Substitute.For<IGoogleWorkspaceUserService>();
        var userEmail = Substitute.For<IUserEmailService>();
        var email = Substitute.For<IEmailService>();
        var notify = Substitute.For<INotificationService>();
        var audit = Substitute.For<IAuditLogService>();

        var service = new EmailProvisioningService(
            db, userManager, workspace, userEmail, email, notify, audit,
            NullLogger<EmailProvisioningService>.Instance);

        return new ProvisioningFixture(service, db, workspace, userEmail, email, notify, audit);
    }

    [Fact]
    public async Task ProvisionNobodiesEmailAsync_RejectsWhenEmailBelongsToAnotherUserEmail()
    {
        var f = BuildFixture();
        using var _ = f.DbContext;

        var ownerId = Guid.NewGuid();
        var targetId = Guid.NewGuid();

        f.DbContext.Users.Add(new User
        {
            Id = ownerId,
            Email = "owner@example.com",
            DisplayName = "Owner",
            Profile = new Profile { FirstName = "Owner", LastName = "One" },
        });
        f.DbContext.Users.Add(new User
        {
            Id = targetId,
            Email = "target@example.com",
            DisplayName = "Target",
            Profile = new Profile { FirstName = "Target", LastName = "Two" },
        });
        f.DbContext.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = ownerId,
            Email = "alice@nobodies.team",
            IsVerified = true,
        });
        await f.DbContext.SaveChangesAsync();

        var result = await f.Service.ProvisionNobodiesEmailAsync(targetId, "alice", targetId);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("already in use by another human");

        // No Workspace call, no DB mutation
        await f.WorkspaceUserService.DidNotReceive().GetAccountAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await f.WorkspaceUserService.DidNotReceive().ProvisionAccountAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await f.UserEmailService.DidNotReceive().AddVerifiedEmailAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Target's GoogleEmail was NOT mutated
        var target = await f.DbContext.Users.FindAsync(targetId);
        target!.GoogleEmail.Should().BeNull();
    }

    [Fact]
    public async Task ProvisionNobodiesEmailAsync_RejectsWhenEmailBelongsToAnotherGoogleEmail()
    {
        var f = BuildFixture();
        using var _ = f.DbContext;

        var ownerId = Guid.NewGuid();
        var targetId = Guid.NewGuid();

        f.DbContext.Users.Add(new User
        {
            Id = ownerId,
            Email = "owner@example.com",
            DisplayName = "Owner",
            GoogleEmail = "alice@nobodies.team",
            Profile = new Profile { FirstName = "Owner", LastName = "One" },
        });
        f.DbContext.Users.Add(new User
        {
            Id = targetId,
            Email = "target@example.com",
            DisplayName = "Target",
            Profile = new Profile { FirstName = "Target", LastName = "Two" },
        });
        await f.DbContext.SaveChangesAsync();

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

        var target = await f.DbContext.Users.FindAsync(targetId);
        target!.GoogleEmail.Should().BeNull();
    }

    [Fact]
    public async Task ProvisionNobodiesEmailAsync_RejectsWhenTargetAlsoHasRowForSameEmail()
    {
        // Regression for the FirstOrDefault-then-compare bug: if the target user
        // has a row for the same email AND another user also has one, the original
        // code could return the target's row and miss the cross-user conflict.
        // Filtering UserId != userId inside the query is deterministic.
        var f = BuildFixture();
        using var _ = f.DbContext;

        var ownerId = Guid.NewGuid();
        var targetId = Guid.NewGuid();

        f.DbContext.Users.Add(new User
        {
            Id = ownerId,
            Email = "owner@example.com",
            DisplayName = "Owner",
            Profile = new Profile { FirstName = "Owner", LastName = "One" },
        });
        f.DbContext.Users.Add(new User
        {
            Id = targetId,
            Email = "target@example.com",
            DisplayName = "Target",
            Profile = new Profile { FirstName = "Target", LastName = "Two" },
        });
        // Target's row goes in FIRST so it's more likely to be returned by FirstOrDefault
        f.DbContext.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = targetId,
            Email = "alice@nobodies.team",
            IsVerified = true,
        });
        f.DbContext.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = ownerId,
            Email = "alice@nobodies.team",
            IsVerified = true,
        });
        await f.DbContext.SaveChangesAsync();

        var result = await f.Service.ProvisionNobodiesEmailAsync(targetId, "alice", targetId);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("already in use by another human");

        await f.WorkspaceUserService.DidNotReceive().ProvisionAccountAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProvisionNobodiesEmailAsync_RejectsWhenPrefixCollidesWithTeamGoogleGroup()
    {
        var f = BuildFixture();
        using var _ = f.DbContext;

        var userId = Guid.NewGuid();
        f.DbContext.Users.Add(new User
        {
            Id = userId,
            Email = "person@example.com",
            DisplayName = "Person",
            Profile = new Profile { FirstName = "Person", LastName = "Test" },
        });
        f.DbContext.Teams.Add(new Team
        {
            Id = Guid.NewGuid(),
            Name = "Communications",
            Slug = "communications",
            IsActive = true,
            GoogleGroupPrefix = "comms",
        });
        await f.DbContext.SaveChangesAsync();

        var result = await f.Service.ProvisionNobodiesEmailAsync(userId, "comms", userId);

        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Contain("Google Group");
        result.ErrorMessage.Should().Contain("Communications");

        // No Workspace call, no DB mutation
        await f.WorkspaceUserService.DidNotReceive().GetAccountAsync(
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        await f.WorkspaceUserService.DidNotReceive().ProvisionAccountAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
        await f.UserEmailService.DidNotReceive().AddVerifiedEmailAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ProvisionNobodiesEmailAsync_AllowsWhenPrefixIsFree()
    {
        var f = BuildFixture();
        using var _ = f.DbContext;

        var userId = Guid.NewGuid();
        f.DbContext.Users.Add(new User
        {
            Id = userId,
            Email = "person@example.com",
            DisplayName = "Person",
            Profile = new Profile { FirstName = "Person", LastName = "Test" },
        });
        await f.DbContext.SaveChangesAsync();

        f.WorkspaceUserService.GetAccountAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((WorkspaceUserAccount?)null);
        f.WorkspaceUserService.ProvisionAccountAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(),
                Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new WorkspaceUserAccount(
                "bob@nobodies.team", "Person", "Test", false,
                DateTime.UtcNow, null));

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
