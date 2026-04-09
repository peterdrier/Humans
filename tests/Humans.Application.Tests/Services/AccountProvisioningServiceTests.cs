using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services;

file sealed class StubAuditLog : IAuditLogService
{
    public Task LogAsync(AuditAction action, string entityType, Guid entityId,
        string description, string jobName,
        Guid? relatedEntityId = null, string? relatedEntityType = null) => Task.CompletedTask;

    public Task LogAsync(AuditAction action, string entityType, Guid entityId,
        string description, Guid actorUserId,
        Guid? relatedEntityId = null, string? relatedEntityType = null) => Task.CompletedTask;

    public Task LogGoogleSyncAsync(AuditAction action, Guid resourceId,
        string description, string jobName,
        string userEmail, string role, GoogleSyncSource source, bool success,
        string? errorMessage = null,
        Guid? relatedEntityId = null, string? relatedEntityType = null) => Task.CompletedTask;

    public Task<IReadOnlyList<AuditLogEntry>> GetByResourceAsync(Guid resourceId) =>
        Task.FromResult<IReadOnlyList<AuditLogEntry>>(Array.Empty<AuditLogEntry>());

    public Task<IReadOnlyList<AuditLogEntry>> GetGoogleSyncByUserAsync(Guid userId) =>
        Task.FromResult<IReadOnlyList<AuditLogEntry>>(Array.Empty<AuditLogEntry>());

    public Task<IReadOnlyList<AuditLogEntry>> GetRecentAsync(int count, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AuditLogEntry>>(Array.Empty<AuditLogEntry>());

    public Task<(IReadOnlyList<AuditLogEntry> Items, int TotalCount, int AnomalyCount)> GetFilteredAsync(
        string? actionFilter, int page, int pageSize, CancellationToken ct = default) =>
        Task.FromResult<(IReadOnlyList<AuditLogEntry>, int, int)>((Array.Empty<AuditLogEntry>(), 0, 0));

    public Task<IReadOnlyList<AuditLogEntry>> GetByUserAsync(Guid userId, int count, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AuditLogEntry>>(Array.Empty<AuditLogEntry>());

    public Task<IReadOnlyList<AuditLogEntry>> GetFilteredEntriesAsync(
        string? entityType = null,
        Guid? entityId = null,
        Guid? userId = null,
        IReadOnlyList<AuditAction>? actions = null,
        int limit = 20,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AuditLogEntry>>(Array.Empty<AuditLogEntry>());

    public Task<AuditLogPageResult> GetAuditLogPageAsync(
        string? actionFilter, int page, int pageSize, CancellationToken ct = default) =>
        Task.FromResult(new AuditLogPageResult(
            Array.Empty<AuditLogEntry>(), 0, 0,
            new Dictionary<Guid, string>(),
            new Dictionary<Guid, (string Name, string Slug)>()));

    public Task<Dictionary<Guid, string>> GetUserDisplayNamesAsync(IReadOnlyList<Guid> userIds, CancellationToken ct = default) =>
        Task.FromResult(new Dictionary<Guid, string>());

    public Task<Dictionary<Guid, (string Name, string Slug)>> GetTeamNamesAsync(IReadOnlyList<Guid> teamIds, CancellationToken ct = default) =>
        Task.FromResult(new Dictionary<Guid, (string Name, string Slug)>());
}

public class AccountProvisioningServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly FakeClock _clock;
    private readonly UserManager<User> _userManager;
    private readonly AccountProvisioningService _service;

    public AccountProvisioningServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _clock = new FakeClock(Instant.FromUtc(2026, 4, 8, 12, 0));

        var store = Substitute.For<IUserStore<User>>();
        _userManager = Substitute.For<UserManager<User>>(
            store, null, null, null, null, null, null, null, null);

        // Mock CreateAsync to add user to DbContext and return success
        _userManager.CreateAsync(Arg.Any<User>())
            .Returns(callInfo =>
            {
                var user = callInfo.Arg<User>();
                if (user.Id == Guid.Empty)
                    user.Id = Guid.NewGuid();
                _dbContext.Users.Add(user);
                _dbContext.SaveChanges();
                return Task.FromResult(IdentityResult.Success);
            });

        _service = new AccountProvisioningService(
            _dbContext,
            _userManager,
            new StubAuditLog(),
            _clock,
            NullLogger<AccountProvisioningService>.Instance);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task FindOrCreateUserByEmailAsync_CreatesNewUser_WhenNoExistingAccount()
    {
        var result = await _service.FindOrCreateUserByEmailAsync(
            "alice@example.com", "Alice Smith", ContactSource.TicketTailor);

        result.Created.Should().BeTrue();
        result.User.Email.Should().Be("alice@example.com");
        result.User.DisplayName.Should().Be("Alice Smith");
        result.User.ContactSource.Should().Be(ContactSource.TicketTailor);

        // Verify UserEmail was created
        var userEmail = await _dbContext.UserEmails.FirstOrDefaultAsync(
            ue => ue.UserId == result.User.Id);
        userEmail.Should().NotBeNull();
        userEmail!.Email.Should().Be("alice@example.com");
        userEmail.IsNotificationTarget.Should().BeTrue();
        userEmail.IsVerified.Should().BeTrue();
    }

    [Fact]
    public async Task FindOrCreateUserByEmailAsync_FindsExisting_ByPrimaryEmail()
    {
        // Seed an existing user
        var existingUser = new User
        {
            Id = Guid.NewGuid(),
            UserName = "bob@example.com",
            Email = "bob@example.com",
            NormalizedEmail = "BOB@EXAMPLE.COM",
            NormalizedUserName = "BOB@EXAMPLE.COM",
            DisplayName = "Bob",
            ContactSource = ContactSource.MailerLite,
            CreatedAt = _clock.GetCurrentInstant(),
        };
        _dbContext.Users.Add(existingUser);
        _dbContext.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = existingUser.Id,
            Email = "bob@example.com",
            IsOAuth = true,
            IsVerified = true,
            IsNotificationTarget = true,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant(),
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.FindOrCreateUserByEmailAsync(
            "bob@example.com", "Bob Jones", ContactSource.TicketTailor);

        result.Created.Should().BeFalse();
        result.User.Id.Should().Be(existingUser.Id);
    }

    [Fact]
    public async Task FindOrCreateUserByEmailAsync_FindsExisting_BySecondaryEmail()
    {
        // Seed a user with a secondary email
        var existingUser = new User
        {
            Id = Guid.NewGuid(),
            UserName = "carol@primary.com",
            Email = "carol@primary.com",
            NormalizedEmail = "CAROL@PRIMARY.COM",
            NormalizedUserName = "CAROL@PRIMARY.COM",
            DisplayName = "Carol",
            CreatedAt = _clock.GetCurrentInstant(),
        };
        _dbContext.Users.Add(existingUser);
        _dbContext.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = existingUser.Id,
            Email = "carol@primary.com",
            IsOAuth = true,
            IsVerified = true,
            IsNotificationTarget = true,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant(),
        });
        _dbContext.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = existingUser.Id,
            Email = "carol@secondary.com",
            IsOAuth = false,
            IsVerified = true,
            IsNotificationTarget = false,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant(),
        });
        await _dbContext.SaveChangesAsync();

        // Look up by secondary email
        var result = await _service.FindOrCreateUserByEmailAsync(
            "carol@secondary.com", "Carol", ContactSource.TicketTailor);

        result.Created.Should().BeFalse();
        result.User.Id.Should().Be(existingUser.Id);
    }

    [Fact]
    public async Task FindOrCreateUserByEmailAsync_DoesNotCreateDuplicates()
    {
        // Create the first time
        var result1 = await _service.FindOrCreateUserByEmailAsync(
            "dave@example.com", "Dave", ContactSource.TicketTailor);

        result1.Created.Should().BeTrue();

        // Call again with the same email
        var result2 = await _service.FindOrCreateUserByEmailAsync(
            "dave@example.com", "Dave Smith", ContactSource.MailerLite);

        result2.Created.Should().BeFalse();
        result2.User.Id.Should().Be(result1.User.Id);
    }

    [Fact]
    public async Task FindOrCreateUserByEmailAsync_HandlesMultipleSources()
    {
        // First call from TicketTailor
        var result1 = await _service.FindOrCreateUserByEmailAsync(
            "emma@example.com", "Emma", ContactSource.TicketTailor);

        result1.Created.Should().BeTrue();
        result1.User.ContactSource.Should().Be(ContactSource.TicketTailor);

        // Second call from MailerLite — should find existing, not re-create
        var result2 = await _service.FindOrCreateUserByEmailAsync(
            "emma@example.com", "Emma Jones", ContactSource.MailerLite);

        result2.Created.Should().BeFalse();
        result2.User.Id.Should().Be(result1.User.Id);
        // ContactSource remains the original (first source wins)
        result2.User.ContactSource.Should().Be(ContactSource.TicketTailor);
    }

    [Fact]
    public async Task FindOrCreateUserByEmailAsync_SetsContactSource_OnSelfRegisteredUser()
    {
        // Seed a self-registered user (no ContactSource)
        var existingUser = new User
        {
            Id = Guid.NewGuid(),
            UserName = "frank@example.com",
            Email = "frank@example.com",
            NormalizedEmail = "FRANK@EXAMPLE.COM",
            NormalizedUserName = "FRANK@EXAMPLE.COM",
            DisplayName = "Frank",
            ContactSource = null,
            CreatedAt = _clock.GetCurrentInstant(),
        };
        _dbContext.Users.Add(existingUser);
        _dbContext.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = existingUser.Id,
            Email = "frank@example.com",
            IsOAuth = true,
            IsVerified = true,
            IsNotificationTarget = true,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant(),
        });
        await _dbContext.SaveChangesAsync();

        var result = await _service.FindOrCreateUserByEmailAsync(
            "frank@example.com", "Frank", ContactSource.TicketTailor);

        result.Created.Should().BeFalse();
        result.User.Id.Should().Be(existingUser.Id);
        // ContactSource should now be set
        result.User.ContactSource.Should().Be(ContactSource.TicketTailor);
    }

    [Fact]
    public async Task FindOrCreateUserByEmailAsync_UsesEmailPrefix_WhenDisplayNameEmpty()
    {
        var result = await _service.FindOrCreateUserByEmailAsync(
            "grace@example.com", null, ContactSource.MailerLite);

        result.Created.Should().BeTrue();
        result.User.DisplayName.Should().Be("grace");
    }

    [Fact]
    public async Task FindOrCreateUserByEmailAsync_IsIdempotent_MultipleCalls()
    {
        var result1 = await _service.FindOrCreateUserByEmailAsync(
            "henry@example.com", "Henry", ContactSource.TicketTailor);

        var result2 = await _service.FindOrCreateUserByEmailAsync(
            "henry@example.com", "Henry", ContactSource.TicketTailor);

        var result3 = await _service.FindOrCreateUserByEmailAsync(
            "henry@example.com", "Henry", ContactSource.TicketTailor);

        result1.User.Id.Should().Be(result2.User.Id);
        result2.User.Id.Should().Be(result3.User.Id);
        result1.Created.Should().BeTrue();
        result2.Created.Should().BeFalse();
        result3.Created.Should().BeFalse();

        // Only one user should exist
        var userCount = await _dbContext.Users.CountAsync(u => u.Email == "henry@example.com");
        userCount.Should().Be(1);
    }
}
