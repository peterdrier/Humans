using AwesomeAssertions;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Services.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.Helpers;
using Microsoft.AspNetCore.Identity;
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

/// <summary>
/// Unit tests for the Application-layer <see cref="AccountProvisioningService"/>
/// (§15 migration, issue #558). Repositories are stubbed in-memory so these
/// tests do not depend on Npgsql-specific translations (<c>ILike</c>) that
/// the EF InMemory provider does not support; behaviour of the actual
/// <see cref="UserEmail"/> / <see cref="User"/> matching is covered in
/// integration/QA against Postgres.
/// </summary>
public class AccountProvisioningServiceTests
{
    private sealed class FakeUserRepository : IUserRepository
    {
        private readonly Dictionary<Guid, User> _users = new();

        public void Seed(User user) => _users[user.Id] = user;
        public bool Contains(Guid userId) => _users.ContainsKey(userId);
        public int Count => _users.Count;
        public IReadOnlyCollection<User> All => _users.Values;

        public Task<User?> GetByIdAsync(Guid userId, CancellationToken ct = default)
            => Task.FromResult(_users.TryGetValue(userId, out var u) ? u : null);

        public Task<User?> GetByEmailOrAlternateAsync(
            string normalizedEmail, string? alternateEmail, CancellationToken ct = default)
        {
            foreach (var user in _users.Values)
            {
                if (Matches(user.Email, normalizedEmail, alternateEmail) ||
                    Matches(user.GoogleEmail, normalizedEmail, alternateEmail))
                {
                    return Task.FromResult<User?>(user);
                }
            }

            return Task.FromResult<User?>(null);
        }

        public Task<bool> SetContactSourceIfNullAsync(
            Guid userId, ContactSource source, CancellationToken ct = default)
        {
            if (!_users.TryGetValue(userId, out var user))
                return Task.FromResult(false);

            if (user.ContactSource is not null)
                return Task.FromResult(false);

            user.ContactSource = source;
            return Task.FromResult(true);
        }

        private static bool Matches(string? value, string normalized, string? alternate)
        {
            if (value is null) return false;
            var v = EmailNormalization.NormalizeForComparison(value);
            if (string.Equals(v, normalized, StringComparison.OrdinalIgnoreCase)) return true;
            if (alternate is not null && string.Equals(v, alternate, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // --- Methods not exercised by these tests ---
        public Task<IReadOnlyDictionary<Guid, User>> GetByIdsAsync(
            IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<User>> GetAllAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<User?> GetByNormalizedEmailAsync(string? normalizedEmail, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<User>> GetContactUsersAsync(string? search, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<Instant>> GetLoginTimestampsInWindowAsync(
            Instant fromInclusive, Instant toExclusive, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> UpdateDisplayNameAsync(Guid userId, string displayName, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> TrySetGoogleEmailAsync(Guid userId, string email, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> SetDeletionPendingAsync(
            Guid userId, Instant requestedAt, Instant scheduledFor, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> ClearDeletionAsync(Guid userId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<EventParticipation?> GetParticipationAsync(
            Guid userId, int year, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<EventParticipation>> GetAllParticipationsForYearAsync(
            int year, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<EventParticipation?> UpsertParticipationAsync(
            Guid userId, int year, ParticipationStatus status,
            ParticipationSource source, Instant? declaredAt, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> RemoveParticipationAsync(
            Guid userId, int year, ParticipationSource requiredSource, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<int> BackfillParticipationsAsync(
            int year, IReadOnlyList<(Guid UserId, ParticipationStatus Status)> entries,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> AnonymizeForMergeAsync(Guid userId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task RemoveExternalLoginsAsync(Guid userId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task MigrateExternalLoginsAsync(
            Guid sourceUserId, Guid targetUserId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<Guid?> GetOtherUserIdHavingGoogleEmailAsync(
            string email, Guid excludeUserId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> SetGoogleEmailAsync(Guid userId, string email, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<(bool Updated, string? OldEmail)> RewritePrimaryEmailAsync(Guid userId, string newEmail, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<Guid>> GetAllUserIdsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<(string Language, int Count)>>
            GetLanguageDistributionForUserIdsAsync(
                IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<string?> PurgeAsync(Guid userId, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private sealed class FakeUserEmailRepository : IUserEmailRepository
    {
        private readonly Dictionary<Guid, UserEmail> _emails = new();

        public void Seed(UserEmail email) => _emails[email.Id] = email;
        public int Count => _emails.Count;
        public IReadOnlyCollection<UserEmail> All => _emails.Values;

        public Task<UserEmail?> FindByNormalizedEmailAsync(
            string normalizedEmail, string? alternateEmail, CancellationToken ct = default)
        {
            foreach (var ue in _emails.Values)
            {
                var n = EmailNormalization.NormalizeForComparison(ue.Email);
                if (string.Equals(n, normalizedEmail, StringComparison.OrdinalIgnoreCase)) return Task.FromResult<UserEmail?>(ue);
                if (alternateEmail is not null && string.Equals(n, alternateEmail, StringComparison.OrdinalIgnoreCase)) return Task.FromResult<UserEmail?>(ue);
            }

            return Task.FromResult<UserEmail?>(null);
        }

        public Task AddAsync(UserEmail email, CancellationToken ct = default)
        {
            _emails[email.Id] = email;
            return Task.CompletedTask;
        }

        // --- Methods not exercised by these tests ---
        public Task<IReadOnlyList<UserEmail>> GetByUserIdReadOnlyAsync(Guid userId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<UserEmail>> GetByUserIdForMutationAsync(Guid userId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<UserEmail?> GetByIdAndUserIdAsync(Guid emailId, Guid userId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<UserEmail?> GetByIdReadOnlyAsync(Guid emailId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> ExistsForUserAsync(Guid userId, string normalizedEmail, string? alternateEmail, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> ExistsVerifiedForOtherUserAsync(Guid userId, string normalizedEmail, string? alternateEmail, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<UserEmail?> GetConflictingVerifiedEmailAsync(Guid excludeEmailId, string normalizedEmail, string? alternateEmail, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<int> GetMaxDisplayOrderAsync(Guid userId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<UserEmail>> GetAllVerifiedNobodiesTeamEmailsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<Dictionary<Guid, string>> GetAllNotificationTargetEmailsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<string?> GetVerifiedEmailAddressAsync(Guid userId, Guid emailId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task RemoveAsync(UserEmail email, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task RemoveAllForUserAsync(Guid userId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<Humans.Application.DTOs.UserEmailWithUser?> FindVerifiedWithUserAsync(string normalizedEmail, string? alternateEmail, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task UpdateAsync(UserEmail email, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task UpdateBatchAsync(IReadOnlyList<UserEmail> emails, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<Guid?> GetUserIdByVerifiedEmailAsync(string email, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<UserEmail>> GetAllAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task RemoveAllForUserAndSaveAsync(Guid userId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> MarkVerifiedAsync(Guid emailId, Instant now, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> RemoveByIdAsync(Guid emailId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<Guid>> SearchUserIdsByVerifiedEmailAsync(
            string searchTerm, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<Guid?> GetOtherUserIdHavingEmailAsync(
            string email, Guid excludeUserId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<UserEmail>> GetByEmailsAsync(
            IReadOnlyCollection<string> emails, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> AnyWithEmailAsync(string email, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> RewriteOAuthEmailAsync(Guid userId, string newEmail, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> RewriteEmailAddressAsync(
            Guid userId, string oldEmail, string newEmail, Instant now, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    private readonly FakeClock _clock;
    private readonly UserManager<User> _userManager;
    private readonly FakeUserRepository _userRepo;
    private readonly FakeUserEmailRepository _userEmailRepo;
    private readonly AccountProvisioningService _service;

    public AccountProvisioningServiceTests()
    {
        _clock = new FakeClock(Instant.FromUtc(2026, 4, 8, 12, 0));

        var store = Substitute.For<IUserStore<User>>();
        _userManager = Substitute.For<UserManager<User>>(
            store, null, null, null, null, null, null, null, null);

        _userRepo = new FakeUserRepository();
        _userEmailRepo = new FakeUserEmailRepository();

        // Mock CreateAsync to persist the user into the fake repo and return success.
        _userManager.CreateAsync(Arg.Any<User>())
            .Returns(callInfo =>
            {
                var user = callInfo.Arg<User>();
                if (user.Id == Guid.Empty)
                    user.Id = Guid.NewGuid();
                _userRepo.Seed(user);
                return Task.FromResult(IdentityResult.Success);
            });

        _service = new AccountProvisioningService(
            _userRepo,
            _userEmailRepo,
            _userManager,
            new StubAuditLog(),
            _clock,
            NullLogger<AccountProvisioningService>.Instance);
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
        var userEmail = _userEmailRepo.All.FirstOrDefault(ue => ue.UserId == result.User.Id);
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
        _userRepo.Seed(existingUser);
        _userEmailRepo.Seed(new UserEmail
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
        _userRepo.Seed(existingUser);
        _userEmailRepo.Seed(new UserEmail
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
        _userEmailRepo.Seed(new UserEmail
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
        _userRepo.Seed(existingUser);
        _userEmailRepo.Seed(new UserEmail
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
        _userRepo.All.Count(u => string.Equals(u.Email, "henry@example.com", StringComparison.Ordinal)).Should().Be(1);
    }
}
