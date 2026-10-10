using System.Transactions;
using NSubstitute.ExceptionExtensions;
using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Users.Contracts;
using Humans.Users.Services;
using Humans.Base.Enums;
using Humans.Base.Helpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Users.Data.Repositories;

namespace Humans.Users.Tests.Services;

file sealed class StubAuditLog : IAuditLogService
{
    public Task LogAsync(AuditAction action, string entityType, Guid entityId,
        string description, string jobName,
        Guid? relatedEntityId = null, string? relatedEntityType = null) => Task.CompletedTask;

    public Task LogAsync(AuditAction action, string entityType, Guid entityId,
        string description, Guid actorUserId,
        Guid? relatedEntityId = null, string? relatedEntityType = null) => Task.CompletedTask;

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

    public Task<IReadOnlySet<Guid>> GetEntityIdsForEntityTypeActionsAsync(
        string entityType, IReadOnlyList<AuditAction> actions, CancellationToken ct = default) =>
        Task.FromResult((IReadOnlySet<Guid>)new HashSet<Guid>());
}

/// <summary>
/// Unit tests for the Application-layer <see cref="AccountProvisioningService"/>.
/// Repositories are stubbed in-memory so these
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

        public void Seed(User user)
        {
            _users[user.Id] = user;
            EnlistRollback(() => _users.Remove(user.Id));
        }
        public void Remove(Guid userId) => _users.Remove(userId);
        public int Count => _users.Count;
        public IReadOnlyCollection<User> All => _users.Values;

        public Task<User?> GetByIdAsync(Guid userId, CancellationToken ct = default)
            => Task.FromResult(_users.TryGetValue(userId, out var u) ? u : null);

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

        // --- Methods not exercised by these tests ---
        public Task<IReadOnlyList<User>> GetAllAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> UpdateDisplayNameAsync(Guid userId, string displayName, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> SetPreferredLanguageAsync(Guid userId, string preferredLanguage, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> SetLastLoginAsync(Guid userId, Instant at, CancellationToken ct = default) =>
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
        public Task ReassignLoginsToUserAsync(
            Guid sourceUserId, Guid targetUserId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task ReassignEventParticipationToUserAsync(
            Guid sourceUserId, Guid targetUserId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task SetLastConsentReminderSentAsync(Guid userId, Instant sentAt, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<Guid>> GetAccountsDueForAnonymizationAsync(Instant now, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<ExpiredDeletionAnonymizationResult?> ApplyExpiredDeletionAnonymizationAsync(Guid userId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<Guid>> GetUserIdsWithExternalLoginsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<int> DeleteUsersAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyDictionary<Guid, IReadOnlyList<(string Provider, string ProviderKey)>>>
            GetExternalLoginsByUserIdsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<ContactField>> GetByProfileIdReadOnlyAsync(
            Guid profileId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<ContactField>> GetAllContactFieldsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<ContactField>> GetByProfileIdForMutationAsync(
            Guid profileId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task BatchSaveAsync(
            IReadOnlyList<ContactField> toAdd,
            IReadOnlyList<ContactField> toUpdate,
            IReadOnlyList<ContactField> toRemove,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task ReassignToUserAsync(
            Guid sourceUserId, Guid targetUserId, Instant updatedAt, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<Profile?> GetByUserIdAsync(Guid userId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<Profile?> GetByUserIdReadOnlyAsync(Guid userId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<Profile>> GetAllProfilesAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<Guid?> GetOwnerUserIdAsync(Guid profileId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<string?> GetProfilePictureContentTypeAsync(
            Guid profileId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<(Guid ProfileId, Guid UserId, string BurnerName, string ContentType, Instant UpdatedAt)>>
            GetCustomPictureRowsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<ProfileLanguage>> GetLanguagesAsync(
            Guid profileId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task ReplaceLanguagesAsync(
            Guid profileId, IReadOnlyList<ProfileLanguage> languages, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task AddAsync(Profile profile, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task UpdateAsync(Profile profile, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> AnonymizeForMergeByUserIdAsync(Guid userId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> AnonymizeForDeletionByUserIdAsync(Guid userId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<int> EraseProfileExtrasForUserAsync(Guid userId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlySet<Guid>> SuspendManyAsync(
            IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<(Guid UserId, MembershipTier NewTier)>> DowngradeTierForExpiredAsync(
            MembershipTier currentTier,
            IReadOnlyCollection<Guid> userIdsToKeep,
            IReadOnlyDictionary<Guid, MembershipTier> fallbackTierByUser,
            Instant now,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task ReassignSubAggregatesToUserAsync(
            Guid sourceUserId, Guid targetUserId, Instant updatedAt, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task ReconcileCVEntriesAsync(
            Guid profileId, IReadOnlyList<CVEntry> entries, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> SetSuspensionAsync(
            Guid userId, bool suspended, bool adminSuspension, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<UserEmail>> GetUserEmailsByUserIdReadOnlyAsync(
            Guid userId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<UserEmail>> GetUserEmailsByUserIdForMutationAsync(
            Guid userId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<UserEmail?> GetUserEmailByIdAndUserIdAsync(
            Guid emailId, Guid userId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<UserEmail?> GetUserEmailByIdReadOnlyAsync(Guid emailId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<UserEmail>> GetUserEmailsByAddressAsync(
            string normalizedEmail, string? alternateEmail, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task ReassignUserEmailsToUserAsync(
            Guid sourceUserId, Guid targetUserId, Instant updatedAt, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<UserEmail>> GetAllUserEmailsAsync(CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<Guid>> GetUserIdsHavingAnyUserEmailAsync(
            IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task RemoveAllUserEmailsForUserAndSaveAsync(Guid userId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task RemoveAllUserEmailsForUsersAndSaveAsync(
            IReadOnlyCollection<Guid> userIds, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> MarkUserEmailVerifiedAsync(
            Guid emailId, Instant now, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<bool> RemoveUserEmailByIdAsync(Guid emailId, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<UserEmail>> GetUserEmailsByEmailsAsync(
            IReadOnlyCollection<string> emails, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<Dictionary<Guid, string>> GetAllNotificationTargetUserEmailsAsync(
            CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task<IReadOnlyList<Guid>> GetUserIdsByUserEmailPrefixAndSuffixAsync(
            string prefix, string suffix, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task ApplyUserEmailReconcilePlanAsync(
            UserEmail? displacedRowToDelete,
            UserEmail? rowToDelete,
            UserEmail? rowToUpdate,
            UserEmail? rowToInsert,
            CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task SetUserEmailGoogleExclusiveAsync(
            Guid userId, Guid userEmailId, Instant updatedAt, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
        public Task AddUserEmailAsync(UserEmail email, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task RemoveUserEmailAsync(UserEmail email, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task UpdateUserEmailAsync(UserEmail email, CancellationToken ct = default) =>
            throw new NotSupportedException();
        public Task UpdateUserEmailsAsync(IReadOnlyList<UserEmail> emails, CancellationToken ct = default) =>
            throw new NotSupportedException();
    }

    /// <summary>
    /// In-memory fake of the two <c>IUserEmailService</c> methods that
    /// <c>AccountProvisioningService</c> calls. AccountProvisioningService routes
    /// UserEmail mutations through <c>IUserEmailService</c> instead of the
    /// repository, so the test fixture mirrors the same boundary.
    /// </summary>
    private sealed class FakeUserEmailService
    {
        private readonly Dictionary<Guid, UserEmail> _emails = new();

        public bool ThrowOnAddVerified { get; set; }
        public bool ThrowOnAddProvisioned { get; set; }

        public void Seed(UserEmail email) => _emails[email.Id] = email;
        public IReadOnlyCollection<UserEmail> All => _emails.Values;

        public Task<IReadOnlyList<UserEmailRowSnapshot>> FindByAddress(string email, bool verifiedOnly)
        {
            var normalizedEmail = EmailNormalization.NormalizeForComparison(email);
            var alternateEmail = GetAlternateEmail(normalizedEmail);
            var rows = _emails.Values
                .Where(ue => !verifiedOnly || ue.IsVerified)
                .Where(ue =>
                {
                    var n = EmailNormalization.NormalizeForComparison(ue.Email);
                    return string.Equals(n, normalizedEmail, StringComparison.OrdinalIgnoreCase)
                        || (alternateEmail is not null && string.Equals(n, alternateEmail, StringComparison.OrdinalIgnoreCase));
                })
                .Select(ue => UserEmailFixtures.Row(ue.UserId, ue.Email, ue.IsVerified, ue.Id) with { Provider = ue.Provider, ProviderKey = ue.ProviderKey })
                .ToList();
            return Task.FromResult<IReadOnlyList<UserEmailRowSnapshot>>(rows);
        }

        public Task<bool> Delete(Guid userId, Guid emailId)
        {
            if (!_emails.TryGetValue(emailId, out var row) || row.UserId != userId) return Task.FromResult(false);
            EnlistRollback(() => _emails[row.Id] = row);
            return Task.FromResult(_emails.Remove(emailId));
        }

        public Task AddProvisioned(Guid userId, string email, Instant now)
        {
            if (ThrowOnAddProvisioned) throw new InvalidOperationException("Email creation failed");
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
                // Simulates the EnsurePrimaryInvariantAsync + EnsureGoogleInvariantAsync
                // orchestrator behaviour for newly-provisioned single-row
                // users — the fresh row becomes both Primary and Google.
                IsPrimary = true,
                IsGoogle = true,
                Visibility = null,
                CreatedAt = now,
                UpdatedAt = now,
            };
            _emails[row.Id] = row;
            EnlistRollback(() => _emails.Remove(row.Id));
            return Task.CompletedTask;
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
    private readonly IUserServiceInternal _userService;
    private readonly AccountProvisioningService _service;
    private readonly IUserInfoInvalidator _invalidator = Substitute.For<IUserInfoInvalidator>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();

    public AccountProvisioningServiceTests()
    {
        _clock = new FakeClock(Instant.FromUtc(2026, 4, 8, 12, 0));

        var store = Substitute.For<IUserStore<User>>();
        _userManager = Substitute.For<UserManager<User>>(
            store, null, null, null, null, null, null, null, null);

        _userRepo = new FakeUserRepository();
        _userEmailFake = new FakeUserEmailService();

        // Wire the fake's methods onto an NSubstitute IUserEmailService —
        // the fixture only needs FindByAddressAsync and the two add commands;
        // everything else stays an unstubbed mock.
        _userEmailService = Substitute.For<IUserEmailService>();
        _userEmailService.FindByAddressAsync(
                Arg.Any<string>(), true, Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(call => _userEmailFake.FindByAddress(call.ArgAt<string>(0), call.ArgAt<bool>(2)));
        _userEmailService.DeleteEmailAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => _userEmailFake.Delete(call.ArgAt<Guid>(0), call.ArgAt<Guid>(1)));
        _userEmailService.AddProvisionedEmailAsync(
                Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => _userEmailFake.AddProvisioned(
                call.ArgAt<Guid>(0), call.ArgAt<string>(1), _clock.GetCurrentInstant()));
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

        _userService = Substitute.For<IUserServiceInternal>();

        _service = new AccountProvisioningService(
            _userRepo,
            _userEmailService,
            _userService,
            _invalidator,
            _userManager,
            _audit,
            _clock,
            NullLogger<AccountProvisioningService>.Instance);
    }

    [HumansTheory]
    [Xunit.InlineData("Identity")]
    [Xunit.InlineData("Email")]
    [Xunit.InlineData("Profile")]
    [Xunit.InlineData("Audit")]
    [Xunit.InlineData("DeletionAudit")]
    public async Task ReplaceUnverified_ProvisioningFailureRollsBackEmailAndAccount(string stage)
    {
        var row = SeedPendingReplacement();
        switch (stage)
        {
            case "Identity":
                _userManager.CreateAsync(Arg.Any<User>()).Returns(IdentityResult.Failed(new IdentityError { Description = "Rejected" }));
                break;
            case "Email": _userEmailFake.ThrowOnAddProvisioned = true; break;
            case "Profile":
                _userService.EnsureStubProfileAsync(Arg.Any<Guid>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
                    .ThrowsAsync(new InvalidOperationException("Profile creation failed"));
                break;
            case "Audit":
                _audit.LogAsync(AuditAction.ContactCreated, nameof(User), Arg.Any<Guid>(), Arg.Any<string>(), nameof(AccountProvisioningService),
                    Arg.Any<Guid?>(), Arg.Any<string?>()).ThrowsAsync(new InvalidOperationException("Audit failed"));
                break;
            case "DeletionAudit":
                _audit.LogAsync(AuditAction.UserEmailDeleted, nameof(UserEmail), row.Id, Arg.Any<string>(), nameof(AccountProvisioningService),
                    row.UserId, nameof(User)).ThrowsAsync(new InvalidOperationException("Deletion audit failed"));
                break;
        }
        _invalidator.InvalidateAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(call => { Transaction.Current.Should().BeNull(); return Task.CompletedTask; });

        Func<Task> act = () => _service.ReplaceUnverifiedEmailAndProvisionAsync(
            row.UserId, row.Id, row.Email, "Victim", ContactSource.TicketTailor, Xunit.TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>();

        _userEmailFake.All.Should().ContainSingle().Which.Should().BeSameAs(row);
        _userRepo.All.Should().ContainSingle().Which.Id.Should().Be(row.UserId);
        await _invalidator.Received(1).InvalidateAsync(row.UserId, CancellationToken.None, Arg.Any<string>(), Arg.Any<string>());
    }

    [HumansFact]
    public async Task ReplaceUnverified_SuccessCommitsFreshVerifiedOwnerAndRefreshesBothUsers()
    {
        var row = SeedPendingReplacement();
        var result = await _service.ReplaceUnverifiedEmailAndProvisionAsync(
            row.UserId, row.Id, row.Email, "Victim", ContactSource.MailerLite, Xunit.TestContext.Current.CancellationToken);

        result.Created.Should().BeTrue();
        result.User.Id.Should().NotBe(row.UserId);
        await _audit.Received(1).LogAsync(AuditAction.UserEmailDeleted, nameof(UserEmail), row.Id,
            Arg.Is<string>(description => description.Contains(result.User.Id.ToString(), StringComparison.Ordinal)),
            nameof(AccountProvisioningService), row.UserId, nameof(User));
        _userEmailFake.All.Should().ContainSingle().Which.UserId.Should().Be(result.User.Id);
        _userEmailFake.All.Single().IsVerified.Should().BeTrue();
        await _invalidator.Received(1).InvalidateAsync(row.UserId, CancellationToken.None, Arg.Any<string>(), Arg.Any<string>());
        await _invalidator.Received(1).InvalidateAsync(result.User.Id, CancellationToken.None, Arg.Any<string>(), Arg.Any<string>());
    }

    [HumansTheory]
    [Xunit.InlineData("Verified")]
    [Xunit.InlineData("Provider")]
    [Xunit.InlineData("Missing")]
    [Xunit.InlineData("Ambiguous")]
    public async Task ReplaceUnverified_StalePlanCannotDeleteOrProvision(string change)
    {
        var row = SeedPendingReplacement();
        if (string.Equals(change, "Verified", StringComparison.Ordinal)) row.IsVerified = true;
        if (string.Equals(change, "Provider", StringComparison.Ordinal)) row.Provider = "Google";
        if (string.Equals(change, "Ambiguous", StringComparison.Ordinal)) _userEmailFake.Seed(new UserEmail {
            Id = Guid.NewGuid(), UserId = Guid.NewGuid(), Email = row.Email, IsVerified = false });
        var plannedEmailId = string.Equals(change, "Missing", StringComparison.Ordinal) ? Guid.NewGuid() : row.Id;
        Func<Task> act = () => _service.ReplaceUnverifiedEmailAndProvisionAsync(
            row.UserId, plannedEmailId, row.Email, "Victim", ContactSource.MailerLite, Xunit.TestContext.Current.CancellationToken);
        await act.Should().ThrowAsync<InvalidOperationException>();
        _userEmailFake.All.Should().Contain(row);
        _userEmailFake.All.Should().HaveCount(string.Equals(change, "Ambiguous", StringComparison.Ordinal) ? 2 : 1);
        await _userEmailService.DidNotReceiveWithAnyArgs().DeleteEmailAsync(default, default, default);
        await _userManager.DidNotReceiveWithAnyArgs().CreateAsync(default!);
    }

    private UserEmail SeedPendingReplacement()
    {
        var userId = Guid.NewGuid();
        _userRepo.Seed(new User { Id = userId, DisplayName = "Existing owner" });
        var row = new UserEmail { Id = Guid.NewGuid(), UserId = userId, Email = "victim@example.com", IsVerified = false };
        _userEmailFake.Seed(row);
        return row;
    }

    // Transaction-aware test stores participate in the real ambient transaction,
    // so failures at later provisioning stages exercise rollback rather than call ordering.
    private static void EnlistRollback(Action rollback)
        => Transaction.Current?.EnlistVolatile(new RollbackWork(rollback), EnlistmentOptions.None);

    private sealed class RollbackWork(Action rollback) : IEnlistmentNotification
    {
        public void Prepare(PreparingEnlistment enlistment) => enlistment.Prepared();
        public void Commit(Enlistment enlistment) => enlistment.Done();
        public void Rollback(Enlistment enlistment) { rollback(); enlistment.Done(); }
        public void InDoubt(Enlistment enlistment) { rollback(); enlistment.Done(); }
    }

    [HumansFact]
    public async Task FindOrCreateUserByEmailAsync_CreatesNewUser_WhenNoExistingAccount()
    {
        var result = await _service.FindOrCreateUserByEmailAsync(
            "alice@example.com", "Alice Smith", ContactSource.TicketTailor, Xunit.TestContext.Current.CancellationToken);

        result.Created.Should().BeTrue();
        // User.Email is no longer the identity — the verified row is.
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
            "bob@example.com", "Bob Jones", ContactSource.TicketTailor, Xunit.TestContext.Current.CancellationToken);

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
            "carol@secondary.com", "Carol", ContactSource.TicketTailor, Xunit.TestContext.Current.CancellationToken);

        result.Created.Should().BeFalse();
        result.User.Id.Should().Be(existingUser.Id);
    }

    [HumansFact]
    public async Task FindOrCreateUserByEmailAsync_DoesNotCreateDuplicates()
    {
        // Create the first time
        var result1 = await _service.FindOrCreateUserByEmailAsync(
            "dave@example.com", "Dave", ContactSource.TicketTailor, Xunit.TestContext.Current.CancellationToken);

        result1.Created.Should().BeTrue();

        // Call again with the same email
        var result2 = await _service.FindOrCreateUserByEmailAsync(
            "dave@example.com", "Dave Smith", ContactSource.MailerLite, Xunit.TestContext.Current.CancellationToken);

        result2.Created.Should().BeFalse();
        result2.User.Id.Should().Be(result1.User.Id);
    }

    [HumansFact]
    public async Task FindOrCreateUserByEmailAsync_HandlesMultipleSources()
    {
        // First call from TicketTailor
        var result1 = await _service.FindOrCreateUserByEmailAsync(
            "emma@example.com", "Emma", ContactSource.TicketTailor, Xunit.TestContext.Current.CancellationToken);

        result1.Created.Should().BeTrue();
        result1.User.ContactSource.Should().Be(ContactSource.TicketTailor);

        // Second call from MailerLite — should find existing, not re-create
        var result2 = await _service.FindOrCreateUserByEmailAsync(
            "emma@example.com", "Emma Jones", ContactSource.MailerLite, Xunit.TestContext.Current.CancellationToken);

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
            "frank@example.com", "Frank", ContactSource.TicketTailor, Xunit.TestContext.Current.CancellationToken);

        result.Created.Should().BeFalse();
        result.User.Id.Should().Be(existingUser.Id);
        // ContactSource should now be set
        result.User.ContactSource.Should().Be(ContactSource.TicketTailor);
    }

    [HumansFact]
    public async Task FindOrCreateUserByEmailAsync_UsesEmailPrefix_WhenDisplayNameEmpty()
    {
        var result = await _service.FindOrCreateUserByEmailAsync(
            "grace@example.com", null, ContactSource.MailerLite, Xunit.TestContext.Current.CancellationToken);

        result.Created.Should().BeTrue();
        result.User.DisplayName.Should().Be("grace");
    }

    [HumansFact]
    public async Task FindOrCreateUserByEmailAsync_IsIdempotent_MultipleCalls()
    {
        var result1 = await _service.FindOrCreateUserByEmailAsync(
            "henry@example.com", "Henry", ContactSource.TicketTailor, Xunit.TestContext.Current.CancellationToken);

        var result2 = await _service.FindOrCreateUserByEmailAsync(
            "henry@example.com", "Henry", ContactSource.TicketTailor, Xunit.TestContext.Current.CancellationToken);

        var result3 = await _service.FindOrCreateUserByEmailAsync(
            "henry@example.com", "Henry", ContactSource.TicketTailor, Xunit.TestContext.Current.CancellationToken);

        result1.User.Id.Should().Be(result2.User.Id);
        result2.User.Id.Should().Be(result3.User.Id);
        result1.Created.Should().BeTrue();
        result2.Created.Should().BeFalse();
        result3.Created.Should().BeFalse();

        // Only one user should exist. User.Email is no longer the identity —
        // assert via the UserEmail row instead.
        _userEmailFake.All
            .Count(ue => string.Equals(ue.Email, "henry@example.com", StringComparison.Ordinal))
            .Should().Be(1);
    }

    [HumansFact]
    public async Task FindOrCreateUserByEmailAsync_NewUser_GetsExactlyOneIsGoogleRow()
    {
        // AccountProvisioningService path enforces the IsGoogle invariant via
        // the IUserEmailService orchestrator — a newly-provisioned user gets
        // exactly one IsGoogle row (the one we just created).
        var result = await _service.FindOrCreateUserByEmailAsync(
            "ivy@example.com", "Ivy", ContactSource.TicketTailor, Xunit.TestContext.Current.CancellationToken);

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
    public async Task FindOrCreateUserByEmailAsync_RoutesEmailRowsThroughIUserEmailService()
    {
        // Email-row policy still routes through IUserEmailService even though
        // Users now owns the underlying repository storage methods.
        var result = await _service.FindOrCreateUserByEmailAsync(
            "jane@example.com", "Jane", ContactSource.MailerLite, Xunit.TestContext.Current.CancellationToken);

        result.Created.Should().BeTrue();
        await _userEmailService.Received(1)
            .FindByAddressAsync("jane@example.com", true, false, Arg.Any<CancellationToken>());
        await _userEmailService.Received(1)
            .AddProvisionedEmailAsync(result.User.Id, "jane@example.com", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task CompleteMagicLinkSignupAsync_CreatesUserEmailAndStubProfile()
    {
        var result = await _service.CompleteMagicLinkSignupAsync(
            "magic@example.com", " Magic Human ", " Legal ", " Surname ", Xunit.TestContext.Current.CancellationToken);

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
            .EnsureStubProfileAsync(result.User.Id, "Magic Human", "Legal", "Surname", Arg.Any<CancellationToken>());
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
            "existing@example.com", "Ignored", "Ignored", "Ignored", Xunit.TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(MagicLinkSignupCompletionOutcome.ExistingUser);
        result.User.Should().BeSameAs(user);
        // Login stamp goes through the single owner of the rule (A1).
        await _userService.Received(1).RecordLoginAsync(user.Id, Arg.Any<CancellationToken>());
        await _userManager.DidNotReceive().CreateAsync(Arg.Any<User>());
    }

    [HumansFact]
    public async Task CompleteMagicLinkSignupAsync_AddVerifiedEmailFailureRollsBackUser()
    {
        _userEmailFake.ThrowOnAddVerified = true;

        var result = await _service.CompleteMagicLinkSignupAsync(
            "rollback@example.com", "Rollback", "First", "Last", Xunit.TestContext.Current.CancellationToken);

        result.Outcome.Should().Be(MagicLinkSignupCompletionOutcome.Failed);
        result.User.Should().BeNull();
        _userRepo.Count.Should().Be(0);
        await _userManager.Received(1).DeleteAsync(Arg.Any<User>());
    }
}
