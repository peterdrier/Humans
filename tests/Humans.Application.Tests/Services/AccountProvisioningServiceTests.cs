using AwesomeAssertions;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.AuditLog;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Domain.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

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

    public Task<IReadOnlyList<AuditLogEntrySnapshot>> GetByResourceAsync(Guid resourceId) =>
        Task.FromResult<IReadOnlyList<AuditLogEntrySnapshot>>([]);

    public Task<IReadOnlyList<AuditLogEntrySnapshot>> GetGoogleSyncByUserAsync(Guid userId) =>
        Task.FromResult<IReadOnlyList<AuditLogEntrySnapshot>>([]);

    public Task<IReadOnlyList<AuditLogEntrySnapshot>> GetRecentAsync(int count, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AuditLogEntrySnapshot>>([]);

    public Task<(IReadOnlyList<AuditLogEntrySnapshot> Items, int TotalCount, int AnomalyCount)> GetFilteredAsync(
        string? actionFilter, int page, int pageSize, CancellationToken ct = default) =>
        Task.FromResult<(IReadOnlyList<AuditLogEntrySnapshot>, int, int)>(([], 0, 0));

    public Task<IReadOnlyList<AuditLogEntrySnapshot>> GetByUserAsync(Guid userId, int count, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AuditLogEntrySnapshot>>([]);

    public Task<IReadOnlyList<AuditLogEntrySnapshot>> GetFilteredEntriesAsync(
        string? entityType = null,
        Guid? entityId = null,
        Guid? userId = null,
        IReadOnlyList<AuditAction>? actions = null,
        int limit = 20,
        CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<AuditLogEntrySnapshot>>([]);

    public Task<IReadOnlyList<Guid>> GetEntityIdsForActionInWindowAsync(
        Instant windowStart, Instant windowEnd, AuditAction action, CancellationToken ct = default) =>
        Task.FromResult((IReadOnlyList<Guid>)[]);

    public Task<IReadOnlySet<Guid>> GetEntityIdsForEntityTypeActionsAsync(
        string entityType, IReadOnlyList<AuditAction> actions, CancellationToken ct = default) =>
        Task.FromResult((IReadOnlySet<Guid>)new HashSet<Guid>());
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
        public void Remove(Guid userId) => _users.Remove(userId);
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
                if (Matches(user.Email, normalizedEmail, alternateEmail))
                {
                    return Task.FromResult<User?>(user);
                }
            }

            return Task.FromResult<User?>(null);
        }

        public Task<IReadOnlyDictionary<Guid, string>> GetLegacyGoogleEmailsAsync(
            IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyDictionary<Guid, string>>(new Dictionary<Guid, string>());

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
        public Task<bool> SetPreferredLanguageAsync(Guid userId, string preferredLanguage, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> SetICalTokenAsync(Guid userId, Guid token, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> TrySetGoogleEmailAsync(Guid userId, string email, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> SetDeletionPendingAsync(
            Guid userId, Instant requestedAt, Instant scheduledFor, Instant? eligibleAfter,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> ClearDeletionAsync(Guid userId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<EventParticipation?> GetParticipationAsync(
            Guid userId, int year, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<EventParticipation>> GetAllParticipationsForYearAsync(
            int year, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<EventParticipation>> GetEventParticipationsByUserIdAsync(
            Guid userId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<EventParticipation>>>
            GetEventParticipationsByUserIdsAsync(
                IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<EventParticipation?> UpsertParticipationAsync(
            Guid userId, int year, ParticipationStatus status,
            ParticipationSource source, Instant? declaredAt, Instant? checkedInAt,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> RemoveParticipationAsync(
            Guid userId, int year, ParticipationSource requiredSource, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<int> BackfillParticipationsAsync(
            int year, IReadOnlyList<(Guid UserId, ParticipationStatus Status)> entries,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> AnonymizeForMergeAsync(
            Guid sourceUserId, Guid targetUserId, Instant now, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<int> ReassignLoginsToUserAsync(
            Guid sourceUserId, Guid targetUserId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<int> ReassignEventParticipationToUserAsync(
            Guid sourceUserId, Guid targetUserId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<Guid?> GetOtherUserIdHavingGoogleEmailAsync(
            string email, Guid excludeUserId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> SetGoogleEmailAsync(Guid userId, string email, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> SetGoogleEmailStatusAsync(Guid userId, GoogleEmailStatus status, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<string?> PurgeAsync(Guid userId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task SetLastConsentReminderSentAsync(Guid userId, Instant sentAt, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<int> GetRejectedGoogleEmailCountAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<int> GetCountByContactSourceAsync(ContactSource source, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<Guid>> GetAccountsDueForAnonymizationAsync(Instant now, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<ExpiredDeletionAnonymizationResult?> ApplyExpiredDeletionAnonymizationAsync(Guid userId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<Guid>> GetMergedSourceIdsAsync(
            Guid targetUserId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<Guid>> GetUsersWithLoginsButNoEmailsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<int> DeleteUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<int> DeleteAllExternalLoginsForUserAsync(Guid userId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<(string Provider, string ProviderKey)>>>
            GetExternalLoginsByUserIdsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// In-memory fake of the two <c>IUserEmailService</c> methods that
    /// <c>AccountProvisioningService</c> calls. Issue
    /// nobodies-collective/Humans#687: AccountProvisioningService routes
    /// UserEmail mutations through <c>IUserEmailService</c> instead of the
    /// repository, so the test fixture mirrors the same boundary.
    /// </summary>
    private sealed class FakeUserEmailService
    {
        private readonly Dictionary<Guid, UserEmail> _emails = new();

        public bool ThrowOnAddVerified { get; set; }

        public void Seed(UserEmail email) => _emails[email.Id] = email;
        public int Count => _emails.Count;
        public IReadOnlyCollection<UserEmail> All => _emails.Values;

        public Task<Guid?> FindAnyUserIdByEmail(string email)
        {
            var normalizedEmail = EmailNormalization.NormalizeForComparison(email);
            var alternateEmail = GetAlternateEmail(normalizedEmail);
            foreach (var ue in _emails.Values)
            {
                var n = EmailNormalization.NormalizeForComparison(ue.Email);
                if (string.Equals(n, normalizedEmail, StringComparison.OrdinalIgnoreCase)) return Task.FromResult<Guid?>(ue.UserId);
                if (alternateEmail is not null && string.Equals(n, alternateEmail, StringComparison.OrdinalIgnoreCase)) return Task.FromResult<Guid?>(ue.UserId);
            }

            return Task.FromResult<Guid?>(null);
        }

        public Task AddProvisioned(Guid userId, string email, Instant now)
        {
            var normalized = EmailNormalization.NormalizeForComparison(email);
            // Idempotent — duplicate provisioning attempts must not re-add the row.
            foreach (var ue in _emails.Values)
            {
                if (ue.UserId != userId) continue;
                var existing = EmailNormalization.NormalizeForComparison(ue.Email);
                if (string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase))
                    return Task.CompletedTask;
            }

            var row = new UserEmail
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Email = email,
                IsVerified = true,
                // Issue nobodies-collective/Humans#687: simulate the
                // EnsurePrimaryInvariantAsync + EnsureGoogleInvariantAsync
                // orchestrator behaviour for newly-provisioned single-row
                // users — the fresh row becomes both Primary and Google.
                IsPrimary = true,
                IsGoogle = true,
                Visibility = null,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _emails[row.Id] = row;
            return Task.CompletedTask;
        }

        public Task<UserEmailWithUser?> FindVerifiedEmailWithUser(string email, FakeUserRepository users)
        {
            var normalizedEmail = EmailNormalization.NormalizeForComparison(email);
            var alternateEmail = GetAlternateEmail(normalizedEmail);
            foreach (var ue in _emails.Values)
            {
                if (!ue.IsVerified)
                    continue;

                var n = EmailNormalization.NormalizeForComparison(ue.Email);
                if (!string.Equals(n, normalizedEmail, StringComparison.OrdinalIgnoreCase)
                    && (alternateEmail is null || !string.Equals(n, alternateEmail, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var user = users.All.FirstOrDefault(u => u.Id == ue.UserId);
                return Task.FromResult<UserEmailWithUser?>(new UserEmailWithUser(
                    ue.UserId,
                    ue.Email,
                    user?.ContactSource,
                    user?.LastLoginAt));
            }

            return Task.FromResult<UserEmailWithUser?>(null);
        }

        public Task<bool> AddVerified(Guid userId, string email, Instant now)
        {
            if (ThrowOnAddVerified)
                throw new InvalidOperationException("boom");

            var normalized = EmailNormalization.NormalizeForComparison(email);
            foreach (var ue in _emails.Values)
            {
                if (ue.UserId != userId) continue;
                var existing = EmailNormalization.NormalizeForComparison(ue.Email);
                if (string.Equals(existing, normalized, StringComparison.OrdinalIgnoreCase))
                    return Task.FromResult(false);
            }

            var row = new UserEmail
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Email = email,
                IsVerified = true,
                IsPrimary = true,
                IsGoogle = true,
                Visibility = null,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _emails[row.Id] = row;
            return Task.FromResult(true);
        }

        private static string? GetAlternateEmail(string normalizedEmail)
        {
            if (normalizedEmail.EndsWith("@gmail.com", StringComparison.Ordinal))
                return $"{normalizedEmail[..^"@gmail.com".Length]}@googlemail.com";

            return null;
        }
    }

    private readonly FakeClock _clock;
    private readonly UserManager<User> _userManager;
    private readonly FakeUserRepository _userRepo;
    private readonly FakeUserEmailService _userEmailFake;
    private readonly IUserEmailService _userEmailService;
    private readonly IUserService _userService;
    private readonly AccountProvisioningService _service;

    public AccountProvisioningServiceTests()
    {
        _clock = new FakeClock(Instant.FromUtc(2026, 4, 8, 12, 0));

        var store = Substitute.For<IUserStore<User>>();
        _userManager = Substitute.For<UserManager<User>>(
            store, null, null, null, null, null, null, null, null);

        _userRepo = new FakeUserRepository();
        _userEmailFake = new FakeUserEmailService();

        // Wire the fake's two methods onto an NSubstitute IUserEmailService —
        // the fixture only needs FindAnyUserIdByEmailAsync and
        // AddProvisionedEmailAsync; everything else stays an unstubbed mock.
        _userEmailService = Substitute.For<IUserEmailService>();
        _userEmailService.FindAnyUserIdByEmailAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => _userEmailFake.FindAnyUserIdByEmail(call.Arg<string>()));
        _userEmailService.AddProvisionedEmailAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => _userEmailFake.AddProvisioned(
                call.ArgAt<Guid>(0), call.ArgAt<string>(1), _clock.GetCurrentInstant()));
        _userEmailService.FindVerifiedEmailWithUserAsync(
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => _userEmailFake.FindVerifiedEmailWithUser(
                call.Arg<string>(), _userRepo));
        _userEmailService.AddVerifiedEmailAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => _userEmailFake.AddVerified(
                call.ArgAt<Guid>(0), call.ArgAt<string>(1), _clock.GetCurrentInstant()));

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
        _userManager.UpdateAsync(Arg.Any<User>())
            .Returns(IdentityResult.Success);
        _userManager.DeleteAsync(Arg.Any<User>())
            .Returns(callInfo =>
            {
                _userRepo.Remove(callInfo.Arg<User>().Id);
                return Task.FromResult(IdentityResult.Success);
            });

        _userService = Substitute.For<IUserService>();

        _service = new AccountProvisioningService(
            _userRepo,
            _userEmailService,
            _userService,
            _userManager,
            new StubAuditLog(),
            _clock,
            NullLogger<AccountProvisioningService>.Instance);
    }

    [HumansFact]
    public async Task FindOrCreateUserByEmailAsync_CreatesNewUser_WhenNoExistingAccount()
    {
        var result = await _service.FindOrCreateUserByEmailAsync(
            "alice@example.com", "Alice Smith", ContactSource.TicketTailor);

        result.Created.Should().BeTrue();
        // Per PR 1 of email-identity-decoupling spec: User.Email is no longer
        // populated on creation — the UserEmail row carries the email.
        result.User.Email.Should().BeNull();
        result.User.DisplayName.Should().Be("Alice Smith");
        result.User.ContactSource.Should().Be(ContactSource.TicketTailor);

        // Verify UserEmail was created
        var userEmail = _userEmailFake.All.FirstOrDefault(ue => ue.UserId == result.User.Id);
        userEmail.Should().NotBeNull();
        userEmail.Email.Should().Be("alice@example.com");
        userEmail.IsPrimary.Should().BeTrue();
        userEmail.IsVerified.Should().BeTrue();
    }

    [HumansFact]
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
        _userEmailFake.Seed(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = existingUser.Id,
            Email = "bob@example.com",
            IsVerified = true,
            IsPrimary = true,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant(),
        });

        var result = await _service.FindOrCreateUserByEmailAsync(
            "bob@example.com", "Bob Jones", ContactSource.TicketTailor);

        result.Created.Should().BeFalse();
        result.User.Id.Should().Be(existingUser.Id);
    }

    [HumansFact]
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
        _userEmailFake.Seed(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = existingUser.Id,
            Email = "carol@primary.com",
            IsVerified = true,
            IsPrimary = true,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant(),
        });
        _userEmailFake.Seed(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = existingUser.Id,
            Email = "carol@secondary.com",
            IsVerified = true,
            IsPrimary = false,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant(),
        });

        // Look up by secondary email
        var result = await _service.FindOrCreateUserByEmailAsync(
            "carol@secondary.com", "Carol", ContactSource.TicketTailor);

        result.Created.Should().BeFalse();
        result.User.Id.Should().Be(existingUser.Id);
    }

    [HumansFact]
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

    [HumansFact]
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

    [HumansFact]
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
        _userEmailFake.Seed(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = existingUser.Id,
            Email = "frank@example.com",
            IsVerified = true,
            IsPrimary = true,
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

    [HumansFact]
    public async Task FindOrCreateUserByEmailAsync_UsesEmailPrefix_WhenDisplayNameEmpty()
    {
        var result = await _service.FindOrCreateUserByEmailAsync(
            "grace@example.com", null, ContactSource.MailerLite);

        result.Created.Should().BeTrue();
        result.User.DisplayName.Should().Be("grace");
    }

    [HumansFact]
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

        // Only one user should exist. Per PR 1 of email-identity-decoupling
        // spec, User.Email is null on newly-created users — assert via the
        // UserEmail row instead.
        _userEmailFake.All
            .Count(ue => string.Equals(ue.Email, "henry@example.com", StringComparison.Ordinal))
            .Should().Be(1);
    }

    [HumansFact]
    public async Task FindOrCreateUserByEmailAsync_NewUser_GetsExactlyOneIsGoogleRow()
    {
        // Issue nobodies-collective/Humans#687 acceptance criterion:
        // AccountProvisioningService path enforces the IsGoogle invariant via
        // the IUserEmailService orchestrator — a newly-provisioned user gets
        // exactly one IsGoogle row (the one we just created).
        var result = await _service.FindOrCreateUserByEmailAsync(
            "ivy@example.com", "Ivy", ContactSource.TicketTailor);

        result.Created.Should().BeTrue();

        var rowsForUser = _userEmailFake.All
            .Where(ue => ue.UserId == result.User.Id)
            .ToList();

        rowsForUser.Should().HaveCount(1);
        rowsForUser[0].IsGoogle.Should().BeTrue();
        rowsForUser[0].IsPrimary.Should().BeTrue();
        rowsForUser[0].IsVerified.Should().BeTrue();
    }

    [HumansFact]
    public async Task FindOrCreateUserByEmailAsync_GoesThroughIUserEmailService_NotIUserEmailRepository()
    {
        // Issue nobodies-collective/Humans#687: AccountProvisioningService no
        // longer references IUserEmailRepository. The fixture wires
        // FindAnyUserIdByEmailAsync + AddProvisionedEmailAsync through the
        // mocked IUserEmailService — if the service called the repository
        // directly the fixture wouldn't satisfy the lookups and the test
        // wouldn't produce a row. The presence of the row proves the route.
        var result = await _service.FindOrCreateUserByEmailAsync(
            "jane@example.com", "Jane", ContactSource.MailerLite);

        result.Created.Should().BeTrue();
        await _userEmailService.Received(1)
            .FindAnyUserIdByEmailAsync("jane@example.com", Arg.Any<CancellationToken>());
        await _userEmailService.Received(1)
            .AddProvisionedEmailAsync(result.User.Id, "jane@example.com", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CompleteMagicLinkSignupAsync_CreatesUserEmailAndStubProfile()
    {
        var result = await _service.CompleteMagicLinkSignupAsync(
            "magic@example.com", " Magic Human ");

        result.Outcome.Should().Be(MagicLinkSignupCompletionOutcome.Created);
        result.User.Should().NotBeNull();
        result.User!.DisplayName.Should().Be("Magic Human");
        result.User.LastLoginAt.Should().Be(_clock.GetCurrentInstant());
        _userEmailFake.All.Should().ContainSingle(ue =>
            ue.UserId == result.User.Id
            && ue.Email == "magic@example.com"
            && ue.IsVerified
            && ue.IsPrimary);
        await _userService.Received(1)
            .EnsureStubProfileAsync(result.User.Id, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CompleteMagicLinkSignupAsync_ExistingVerifiedEmailUpdatesLastLogin()
    {
        var user = new User
        {
            Id = Guid.NewGuid(),
            DisplayName = "Existing",
            CreatedAt = _clock.GetCurrentInstant(),
        };
        _userRepo.Seed(user);
        _userEmailFake.Seed(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Email = "existing@example.com",
            IsVerified = true,
            IsPrimary = true,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant(),
        });

        var result = await _service.CompleteMagicLinkSignupAsync(
            "existing@example.com", "Ignored");

        result.Outcome.Should().Be(MagicLinkSignupCompletionOutcome.ExistingUser);
        result.User.Should().BeSameAs(user);
        user.LastLoginAt.Should().Be(_clock.GetCurrentInstant());
        await _userManager.Received(1).UpdateAsync(user);
        await _userManager.DidNotReceive().CreateAsync(Arg.Any<User>());
    }

    [HumansFact]
    public async Task CompleteMagicLinkSignupAsync_AddVerifiedEmailFailureRollsBackUser()
    {
        _userEmailFake.ThrowOnAddVerified = true;

        var result = await _service.CompleteMagicLinkSignupAsync(
            "rollback@example.com", "Rollback");

        result.Outcome.Should().Be(MagicLinkSignupCompletionOutcome.Failed);
        result.User.Should().BeNull();
        _userRepo.Count.Should().Be(0);
        await _userManager.Received(1).DeleteAsync(Arg.Any<User>());
    }
}
