using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Base.Interfaces;
using Humans.Base.Extensions;
using Humans.Users.Services;
using Humans.Users.Tests.Infrastructure;
using Humans.Users.Data.Repositories;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;
using Humans.Users.Contracts;

namespace Humans.Users.Tests.Services.Users;

public sealed class UserServiceProfileOnboardingMutationTests : ServiceTestHarness
{
    private readonly UserService _service;
    private readonly IFileStorage _fileStorage = Substitute.For<IFileStorage>();
    private readonly ICommunicationPreferenceRepository _communicationPreferenceRepository =
        Substitute.For<ICommunicationPreferenceRepository>();

    public UserServiceProfileOnboardingMutationTests()
    {
        var userRepository = new UserRepository(DbFactory, Clock);
        _communicationPreferenceRepository.GetByUserIdReadOnlyAsync(
                Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CommunicationPreference>>([]));
        _communicationPreferenceRepository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<CommunicationPreference>>([]));

        _service = new UserService(
            userRepository,
            _communicationPreferenceRepository,
            AdminAuthorization,
            _fileStorage,
            Clock,
            NullLogger<UserService>.Instance, AuditLog);
    }

    [HumansTheory]
    [InlineData("invalid", "Validation_EmailAddress")]
    [InlineData("overlong", "Validation_EmailAddress")]
    [InlineData("duplicate", "Users_Profile_EmailAlreadyAdded")]
    [InlineData("missing", "Users_EmailUpdate_AccountUnavailable")]
    public async Task AddUserEmailAsync_ExpectedRefusal_DoesNotAddRow(string kind, string errorKey)
    {
        var userId = Guid.NewGuid();
        var repository = Substitute.For<IUserRepository>();
        if (!string.Equals(kind, "missing", StringComparison.Ordinal))
            repository.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(new User { Id = userId });
        repository.GetUserEmailsByAddressAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(string.Equals(kind, "duplicate", StringComparison.Ordinal)
                ? [new UserEmail { Id = Guid.NewGuid(), UserId = userId, Email = "held@example.com" }]
                : []);
        var service = new UserService(repository, _communicationPreferenceRepository,
            AdminAuthorization, _fileStorage, Clock, NullLogger<UserService>.Instance, AuditLog);

        var email = kind switch
        {
            "invalid" => "invalid",
            "overlong" => new string('a', 245) + "@example.com",
            _ => "held@example.com"
        };
        var result = await service.AddUserEmailAsync(userId,
            new UserEmailAddCommand(email, IsVerified: true),
            TestContext.Current.CancellationToken);

        result.ErrorKey.Should().Be(errorKey);
        result.Added.Should().BeFalse();
        result.EmailId.Should().Be(Guid.Empty);
        await repository.DidNotReceive().AddUserEmailAsync(Arg.Any<UserEmail>(), Arg.Any<CancellationToken>());
        AuditLog.ReceivedCalls().Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddUserEmailAsync_StoredLengthLimit_AcceptsTrimmedBoundary(bool padded)
    {
        var userId = Guid.NewGuid();
        SeedUser(userId);
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var email = new string('a', 244) + "@example.com";

        var result = await _service.AddUserEmailAsync(userId,
            new UserEmailAddCommand(padded ? $"  {email}  " : email, IsVerified: true),
            TestContext.Current.CancellationToken);

        result.Added.Should().BeTrue();
        result.ErrorKey.Should().BeNull();
        var row = await Db.UserEmails.AsNoTracking().SingleAsync(e => e.Id == result.EmailId,
            TestContext.Current.CancellationToken);
        row.Email.Should().Be(email).And.HaveLength(256);
    }

    [HumansTheory]
    [InlineData("unverified-primary")]
    [InlineData("missing-primary")]
    [InlineData("missing-visibility")]
    public async Task UpdateUserEmailAsync_ExpectedRefusal_PreservesEmailFlags(string refusal)
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = Guid.NewGuid();
        var rowId = Guid.NewGuid();
        SeedUser(userId);
        Db.UserEmails.Add(new UserEmail
        {
            Id = rowId,
            UserId = userId,
            Email = "unverified@example.com",
            IsVerified = false,
            IsPrimary = false
        });
        await Db.SaveChangesAsync(ct);
        var targetId = string.Equals(refusal, "unverified-primary", StringComparison.Ordinal) ? rowId : Guid.NewGuid();
        var command = string.Equals(refusal, "missing-visibility", StringComparison.Ordinal)
            ? new UserEmailUpdateCommand(ChangeVisibility: true, Visibility: ContactFieldVisibility.AllActiveProfiles)
            : new UserEmailUpdateCommand(Primary: UserEmailPrimaryChange.MakePrimary);
        var update = () => _service.UpdateUserEmailAsync(userId, targetId, command, ct);

        var result = await update();
        var expectedKey = string.Equals(refusal, "unverified-primary", StringComparison.Ordinal)
            ? "Users_EmailUpdate_UnverifiedTarget" : "Users_EmailUpdate_NotFound";
        result.Should().Be(new UpdateEmailResult(false, expectedKey));
        var row = await Db.UserEmails.AsNoTracking().SingleAsync(e => e.Id == rowId, ct);
        row.IsPrimary.Should().BeFalse();
        row.Visibility.Should().BeNull();
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContributeForUserAsync_EmitsUserProfileSlices_WithOAuthKeySourcedFromProviderColumn(
        bool hasDeletionHold)
    {
        // The JSON key stays "IsOAuth" per memory/code/no-rename-serialized-fields.md
        // (exports are JSON files users download). The value sources from
        // (Provider != null), not IsGoogle.
        var userId = Guid.NewGuid();
        var accountUser = SeedUser(userId);
        Db.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = "g@example.com",
            IsVerified = true,
            IsPrimary = true,
            Provider = "Google",
            ProviderKey = "sub-1",
            IsGoogle = false,
        });
        accountUser.State = hasDeletionHold ? UserState.DeletePending : UserState.Bare;
        var arrival = Clock.GetCurrentInstant();
        accountUser.DeletionRequestedAt = hasDeletionHold ? arrival : null;
        accountUser.DeletionScheduledFor = hasDeletionHold ? arrival + NodaTime.Duration.FromDays(30) : null;
        accountUser.DeletionEligibleAfter = hasDeletionHold ? arrival + NodaTime.Duration.FromDays(60) : null;
        Db.EventParticipations.Add(new EventParticipation
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Year = 2026,
            Status = LegacyParticipationStatus.Attended,
            Source = LegacyParticipationSource.TicketSync,
            CheckedInAt = arrival,
        });
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var slices = await _service.ContributeForUserAsync(userId, TestContext.Current.CancellationToken);

        slices.Select(s => s.SectionName).Should().Contain([
            UserService.Account,
            UserService.LegacyEventParticipations,
            UserService.Profile,
            UserService.ContactFields,
            UserService.UserEmails,
            UserService.VolunteerHistory,
            UserService.Languages,
            UserService.LegacyCommunicationPreferences
        ]);
        System.Text.Json.JsonSerializer.Serialize(slices.Single(s =>
            string.Equals(s.SectionName, UserService.MergedProfiles, StringComparison.Ordinal)).Data)
            .Should().Be("[]");
        var userEmailsSlice = slices.Single(s =>
            string.Equals(s.SectionName, UserService.UserEmails, StringComparison.Ordinal));
        var json = System.Text.Json.JsonSerializer.Serialize(userEmailsSlice.Data);
        json.Should().Contain("\"IsOAuth\":true");
        json.Should().Contain("\"IsNotificationTarget\":true");
        json.Should().NotContain("\"IsPrimary\":");

        var accountSlice = slices.Single(s =>
            string.Equals(s.SectionName, UserService.Account, StringComparison.Ordinal));
        var accountJson = System.Text.Json.JsonSerializer.Serialize(accountSlice.Data);
        accountJson.Should().Contain("\"Email\":\"g@example.com\"");
        using var accountData = System.Text.Json.JsonDocument.Parse(accountJson);
        accountData.RootElement.GetProperty("State").GetString().Should().Be(accountUser.State.ToString());
        accountData.RootElement.GetProperty("DeletionEligibleAfter").GetString()
            .Should().Be(accountUser.DeletionEligibleAfter.ToIso8601());
        accountData.RootElement.GetProperty("DeletionScheduledFor").GetString()
            .Should().Be(accountUser.DeletionScheduledFor.ToIso8601());

        var participationSlice = slices.Single(s =>
            string.Equals(s.SectionName, UserService.LegacyEventParticipations, StringComparison.Ordinal));
        using var participationJson = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(participationSlice.Data));
        participationJson.RootElement.EnumerateArray().Single().GetProperty("CheckedInAt")
            .GetString().Should().Be(arrival.ToIso8601());
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContributeForUserAsync_ExportsRetainedProfilesAcrossMergeChain(bool requestArchivedId)
    {
        var oldestId = Guid.NewGuid();
        var archivedId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        var unrelatedId = Guid.NewGuid();
        foreach (var id in new[] { oldestId, archivedId, survivorId, unrelatedId })
        {
            await SeedUserWithProfileAsync(id);
            var profile = await Db.Profiles.SingleAsync(p => p.UserId == id, TestContext.Current.CancellationToken);
            profile.MedicalConditions = $"Health {id}";
            profile.DietaryPreference = $"Diet {id}";
        }
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        foreach (var (source, target) in new[] { (oldestId, archivedId), (archivedId, survivorId) })
        {
            await _service.ReassignAsync(source, target, Guid.NewGuid(), Clock.GetCurrentInstant(),
                TestContext.Current.CancellationToken);
            await _service.AnonymizeForMergeAsync(source, target, Clock.GetCurrentInstant(),
                TestContext.Current.CancellationToken);
        }

        var slices = await _service.ContributeForUserAsync(requestArchivedId ? oldestId : survivorId,
            TestContext.Current.CancellationToken);

        var mergedSlice = slices.Single(s => string.Equals(s.SectionName, UserService.MergedProfiles, StringComparison.Ordinal));
        using var mergedJson = System.Text.Json.JsonDocument.Parse(
            System.Text.Json.JsonSerializer.Serialize(mergedSlice.Data));
        var profiles = mergedJson.RootElement.EnumerateArray()
            .ToDictionary(p => p.GetProperty("UserId").GetGuid(), p => p.GetProperty("Profile"));
        profiles.Keys.Should().BeEquivalentTo([oldestId, archivedId]);
        foreach (var id in new[] { oldestId, archivedId })
        {
            profiles[id].GetProperty("MedicalConditions").GetString().Should().Be($"Health {id}");
            profiles[id].GetProperty("DietaryPreference").GetString().Should().Be($"Diet {id}");
        }
        using var currentJson = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(
            slices.Single(s => string.Equals(s.SectionName, UserService.Profile, StringComparison.Ordinal)).Data));
        currentJson.RootElement.GetProperty("MedicalConditions").GetString().Should().Be($"Health {survivorId}");
    }

    [HumansFact]
    public async Task ContributeForUserAsync_ExportsOwnEmailLifecycleAndGoogleSyncState()
    {
        var userId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        SeedUser(userId);
        SeedUser(otherUserId);
        var createdAt = Clock.GetCurrentInstant();
        await Db.UserEmails.AddRangeAsync([
            new UserEmail
            {
                Id = Guid.NewGuid(), UserId = userId, Email = "google@example.com",
                IsVerified = true, IsPrimary = true, IsGoogle = true,
                GoogleEmailStatus = LegacyGoogleEmailStatus.Rejected,
                CreatedAt = createdAt,
                UpdatedAt = createdAt.Plus(NodaTime.Duration.FromHours(2)),
                VerificationSentAt = createdAt.Plus(NodaTime.Duration.FromHours(1)),
            },
            new UserEmail
            {
                Id = Guid.NewGuid(), UserId = userId, Email = "manual@example.com",
                CreatedAt = createdAt, UpdatedAt = createdAt,
            },
            new UserEmail
            {
                Id = Guid.NewGuid(), UserId = otherUserId, Email = "other@example.com",
                CreatedAt = createdAt, UpdatedAt = createdAt,
            }], TestContext.Current.CancellationToken);
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var slices = await _service.ContributeForUserAsync(userId, TestContext.Current.CancellationToken);
        var slice = slices.Single(s => string.Equals(s.SectionName, UserService.UserEmails, StringComparison.Ordinal));
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(slice.Data));
        var emails = json.RootElement.EnumerateArray().ToList();
        emails.Should().HaveCount(2);
        var google = emails.Single(e => string.Equals(e.GetProperty("Email").GetString(), "google@example.com", StringComparison.Ordinal));
        google.GetProperty("IsGoogle").GetBoolean().Should().BeTrue();
        google.GetProperty("GoogleEmailStatus").GetString().Should().Be("Rejected");
        google.GetProperty("CreatedAt").GetString().Should().Be("2026-03-01T12:00:00Z");
        google.GetProperty("UpdatedAt").GetString().Should().Be("2026-03-01T14:00:00Z");
        google.GetProperty("VerificationSentAt").GetString().Should().Be("2026-03-01T13:00:00Z");
        var manual = emails.Single(e => string.Equals(e.GetProperty("Email").GetString(), "manual@example.com", StringComparison.Ordinal));
        manual.GetProperty("IsGoogle").GetBoolean().Should().BeFalse();
        manual.GetProperty("GoogleEmailStatus").GetString().Should().Be("Unknown");
        manual.GetProperty("VerificationSentAt").GetString().Should().BeNull();
    }

    [HumansTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ContributeForUserAsync_ExportsKnownOrUnknownSubscriptionTimestamp(bool hasSubscribedAt)
    {
        var userId = Guid.NewGuid();
        SeedUser(userId);
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        var subscribedAt = hasSubscribedAt ? Clock.GetCurrentInstant() : (NodaTime.Instant?)null;
        _communicationPreferenceRepository.GetByUserIdReadOnlyAsync(
                userId, TestContext.Current.CancellationToken)
            .Returns(Task.FromResult<IReadOnlyList<CommunicationPreference>>([
                new CommunicationPreference
                {
                    UserId = userId,
                    Category = LegacyMessageCategory.Marketing,
                    SubscribedAt = subscribedAt,
                    UpdatedAt = Clock.GetCurrentInstant(),
                    UpdateSource = "Profile",
                }
            ]));

        var slices = await _service.ContributeForUserAsync(userId, TestContext.Current.CancellationToken);

        var slice = slices.Single(s => string.Equals(s.SectionName, UserService.LegacyCommunicationPreferences, StringComparison.Ordinal));
        using var json = System.Text.Json.JsonDocument.Parse(System.Text.Json.JsonSerializer.Serialize(slice.Data));
        var preference = json.RootElement[0];
        preference.GetProperty("SubscribedAt").GetString().Should().Be(
            hasSubscribedAt ? "2026-03-01T12:00:00Z" : null);
        preference.GetProperty("UpdateSource").GetString().Should().Be("Profile");
    }

    [HumansFact]
    public async Task GetUsersWithLoginsButNoEmailsAsync_ComposesLoginAndUserEmailRepositories()
    {
        var userWithEmail = SeedUser(Guid.NewGuid(), "Has Email");
        var userWithoutEmail = SeedUser(Guid.NewGuid(), "Ghost Login");
        await Db.Set<IdentityUserLogin<Guid>>().AddRangeAsync(
            new IdentityUserLogin<Guid>
            {
                UserId = userWithEmail.Id,
                LoginProvider = "Google",
                ProviderKey = "has-email-sub",
                ProviderDisplayName = "Google",
            },
            new IdentityUserLogin<Guid>
            {
                UserId = userWithoutEmail.Id,
                LoginProvider = "Google",
                ProviderKey = "ghost-sub",
                ProviderDisplayName = "Google",
            });
        Db.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userWithEmail.Id,
            Email = "has-email@example.com",
            IsVerified = true,
            IsPrimary = true,
        });
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await _service.GetUsersWithLoginsButNoEmailsAsync(TestContext.Current.CancellationToken);

        result.Should().Equal([userWithoutEmail.Id]);
    }

    [HumansFact]
    public async Task GetAllUserInfosAsync_ReadsStoredUserStateWithoutWriting()
    {
        var userId = Guid.NewGuid();
        var user = SeedUser(userId);
        user.State = UserState.Active;
        Db.Profiles.Add(new Profile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BurnerName = "Loaded",
            FirstName = "First",
            LastName = "Last",
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant(),
        });
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var infos = await _service.GetAllUserInfosAsync(TestContext.Current.CancellationToken);

        infos.Single(u => u.Id == userId).State.Should().Be(UserState.Active);
    }

    [HumansFact]
    public async Task ApplyExpiredDeletionAnonymizationAsync_RemovesUserEmailsThroughUserRepository()
    {
        var user = SeedUser(Guid.NewGuid(), "Purge Me");
        user.BurnerName = "Purge Me";
        Db.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Email = "purge-me@example.com",
            IsVerified = true,
            IsPrimary = true,
        });
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await _service.ApplyExpiredDeletionAnonymizationAsync(
            user.Id, TestContext.Current.CancellationToken);

        result!.OriginalBurnerName.Should().Be("Purge Me");
        var remaining = await Db.UserEmails.AsNoTracking()
            .Where(e => e.UserId == user.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        remaining.Should().BeEmpty();
    }

    [HumansFact]
    public async Task SetRejectedAsync_SetsRejectedState()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);

        var result = await _service.SetRejectedAsync(userId, TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        var user = await Db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, TestContext.Current.CancellationToken);
        user.State.Should().Be(UserState.Rejected);
    }

    [HumansFact]
    public async Task SetRejectedAsync_StateSurvivesALaterUnsuspend()
    {
        // Rejection is not a profile column, so the classifier must carry it forward
        // from users.State on every later transition.
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);
        await _service.SetRejectedAsync(userId, TestContext.Current.CancellationToken);

        await _service.SetSuspensionAsync(userId, suspended: false, ct: TestContext.Current.CancellationToken);

        var user = await Db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, TestContext.Current.CancellationToken);
        user.State.Should().Be(UserState.Rejected);
    }

    [HumansFact]
    public async Task LegacyRejectedProfile_WithNonRejectedState_StillReadsAndClassifiesRejected()
    {
        // A legacy rejected profile (profiles.RejectedAt set) whose users.State never
        // recorded Rejected: it reads as rejected, and the next transition stores Rejected.
        var userId = Guid.NewGuid();
        var profileId = await SeedUserWithProfileAsync(userId);
        var profile = await Db.Profiles.FirstAsync(p => p.Id == profileId, TestContext.Current.CancellationToken);
        profile.RejectedAt = Clock.GetCurrentInstant();
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Db.ChangeTracker.Clear();
        (await Db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, TestContext.Current.CancellationToken))
            .State.Should().NotBe(UserState.Rejected);

        var info = await _service.GetRawUserInfoAsync(userId, TestContext.Current.CancellationToken);
        info!.IsRejected.Should().BeTrue();

        await _service.SetSuspensionAsync(userId, suspended: false, ct: TestContext.Current.CancellationToken);
        var user = await Db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, TestContext.Current.CancellationToken);
        user.State.Should().Be(UserState.Rejected);
    }

    [HumansFact]
    public async Task SetSuspensionAsync_True_SetsSuspendedStateWithoutTouchingAdminNotes()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);
        var seeded = await Db.Profiles.FirstAsync(p => p.UserId == userId, TestContext.Current.CancellationToken);
        seeded.AdminNotes = "Prefers email contact";
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Db.ChangeTracker.Clear();

        var result = await _service.SetSuspensionAsync(userId, suspended: true, ct: TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        var profile = await Db.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId, TestContext.Current.CancellationToken);
        var user = await Db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, TestContext.Current.CancellationToken);
        user.State.Should().Be(UserState.Suspended);
        // The reason goes to the audit log and the notification, never onto the profile row.
        profile.AdminNotes.Should().Be("Prefers email contact");
    }

    [HumansFact]
    public async Task SetSuspensionAsync_AdminSuspension_SetsAdminSuspendedState()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);

        var result = await _service.SetSuspensionAsync(
            userId, suspended: true, adminSuspension: true, TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        var user = await Db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, TestContext.Current.CancellationToken);
        user.State.Should().Be(UserState.AdminSuspended);
    }

    [HumansFact]
    public async Task SetSuspensionAsync_False_RestoresActiveWhenIdentityComplete()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);
        var suspended = await Db.Users.FirstAsync(u => u.Id == userId, TestContext.Current.CancellationToken);
        suspended.State = UserState.Suspended;
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        Db.ChangeTracker.Clear();

        var result = await _service.SetSuspensionAsync(userId, suspended: false, ct: TestContext.Current.CancellationToken);

        result.Success.Should().BeTrue();
        var fresh = await Db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, TestContext.Current.CancellationToken);
        fresh.State.Should().Be(UserState.Active);
    }
    [HumansFact]
    public async Task SetDeletionPendingAsync_WhenUpdated_SetsDeletePendingState()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);
        var now = Clock.GetCurrentInstant();

        var result = await _service.SetDeletionPendingAsync(
            userId,
            now,
            now.Plus(NodaTime.Duration.FromDays(30)),
            eligibleAfter: null, ct: TestContext.Current.CancellationToken);

        result.Should().BeTrue();
        var user = await Db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, TestContext.Current.CancellationToken);
        user.State.Should().Be(UserState.DeletePending);
    }

    [HumansFact]
    public async Task ClearDeletionAsync_WhenUpdated_RestoresActiveState()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);
        var user = await Db.Users.FirstAsync(u => u.Id == userId, TestContext.Current.CancellationToken);
        var now = Clock.GetCurrentInstant();
        user.DeletionRequestedAt = now;
        user.DeletionScheduledFor = now.Plus(NodaTime.Duration.FromDays(30));
        user.State = UserState.DeletePending;
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await _service.ClearDeletionAsync(userId, TestContext.Current.CancellationToken);

        result.Should().BeTrue();
        var fresh = await Db.Users.AsNoTracking().FirstAsync(u => u.Id == userId, TestContext.Current.CancellationToken);
        fresh.State.Should().Be(UserState.Active);
    }

    [HumansFact]
    public async Task StateTransitions_NoProfile_ReturnNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        (await _service.SetRejectedAsync(Guid.NewGuid(), ct)).ErrorKey.Should().Be("NotFound");
        (await _service.SetSuspensionAsync(Guid.NewGuid(), suspended: true, ct: ct)).ErrorKey.Should().Be("NotFound");
    }

    [HumansFact(Timeout = 5000)]
    public async Task EnsureStubProfileAsync_TwoConcurrentCallers_OnlyOneAddAsync()
    {
        var userId = Guid.NewGuid();
        SeedUser(userId);
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var fakeRepo = Substitute.For<IUserRepository>();
        Profile? stored = null;
        fakeRepo.GetByIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new User
            {
                Id = userId,
                DisplayName = "Test User",
                PreferredLanguage = "en",
            });
        fakeRepo.GetByUserIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(stored));
        fakeRepo.When(r => r.AddAsync(Arg.Any<Profile>(), Arg.Any<CancellationToken>()))
            .Do(call => stored = call.Arg<Profile>());

        var service = BuildWithUserRepository(fakeRepo);

#pragma warning disable VSTHRD003
        await Task.WhenAll(
            Task.Run(() => service.EnsureStubProfileAsync(userId, ct: TestContext.Current.CancellationToken)),
            Task.Run(() => service.EnsureStubProfileAsync(userId, ct: TestContext.Current.CancellationToken)));
#pragma warning restore VSTHRD003

        await fakeRepo.Received(1).AddAsync(Arg.Any<Profile>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task EnsureStubProfileAsync_WithSeededNames_CreatesActiveProfileWithTrimmedNames()
    {
        var userId = Guid.NewGuid();
        SeedUser(userId);
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await _service.EnsureStubProfileAsync(userId, " Sparkle ", " Ada ", " Lovelace ", TestContext.Current.CancellationToken);

        var profile = await Db.Profiles.SingleAsync(p => p.UserId == userId, TestContext.Current.CancellationToken);
        profile.BurnerName.Should().Be("Sparkle");
        profile.FirstName.Should().Be("Ada");
        profile.LastName.Should().Be("Lovelace");
        var user = await Db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, TestContext.Current.CancellationToken);
        user.State.Should().Be(UserState.Active);
        AuditLog.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task EnsureStubProfileAsync_WithoutNames_CreatesStubProfile()
    {
        var userId = Guid.NewGuid();
        SeedUser(userId);
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var actorId = Guid.NewGuid();
        AuditLog.LogAsync(AuditAction.StubProfileBackfilled, nameof(User), userId,
            Arg.Any<string>(), actorId).Returns(async _ =>
            {
                (await Db.Profiles.AsNoTracking().AnyAsync(p => p.UserId == userId,
                    TestContext.Current.CancellationToken)).Should().BeTrue();
            });
        await _service.EnsureStubProfileAsync(userId, ct: TestContext.Current.CancellationToken, actorUserId: actorId);

        var profile = await Db.Profiles.SingleAsync(p => p.UserId == userId, TestContext.Current.CancellationToken);
        profile.BurnerName.Should().BeEmpty();
        var user = await Db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, TestContext.Current.CancellationToken);
        user.State.Should().Be(UserState.Bare);
        await AuditLog.Received(1).LogAsync(AuditAction.StubProfileBackfilled, nameof(User), userId,
            Arg.Any<string>(), actorId);
        (await _service.EnsureStubProfileAsync(userId, ct: TestContext.Current.CancellationToken, actorUserId: actorId))
            .Should().BeFalse();
        AuditLog.ReceivedCalls().Should().HaveCount(1);
    }

    [HumansFact]
    public async Task EnsureStubProfileAsync_WithoutNames_InheritsBurnerNameAlreadySeededOnUser()
    {
        // OAuth/import account creation seeds User.BurnerName directly,
        // then calls EnsureStubProfileAsync with no names. The stub must inherit that
        // BurnerName, or the Profile->User mirror in UpdateUserStateFromProfileAsync wipes
        // it back to empty on the very same save.
        var userId = Guid.NewGuid();
        var user = SeedUser(userId);
        user.BurnerName = "Seeded Burner";
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await _service.EnsureStubProfileAsync(userId, ct: TestContext.Current.CancellationToken);

        var profile = await Db.Profiles.SingleAsync(p => p.UserId == userId, TestContext.Current.CancellationToken);
        profile.BurnerName.Should().Be("Seeded Burner");
        var fresh = await Db.Users.AsNoTracking().SingleAsync(u => u.Id == userId, TestContext.Current.CancellationToken);
        fresh.BurnerName.Should().Be("Seeded Burner");
        // FirstName/LastName are still blank, so the user stays Bare, not Active.
        fresh.State.Should().Be(UserState.Bare);
    }

    [HumansFact]
    public async Task EnsureStubProfileAsync_MergedTombstone_NoOps()
    {
        var userId = Guid.NewGuid();
        var user = SeedUser(userId);
        user.MergedAt = Clock.GetCurrentInstant();
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await _service.EnsureStubProfileAsync(userId, ct: TestContext.Current.CancellationToken, actorUserId: Guid.NewGuid());

        (await Db.Profiles.AnyAsync(p => p.UserId == userId, TestContext.Current.CancellationToken)).Should().BeFalse();
        AuditLog.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task EnsureStubProfileAsync_DeletedTombstone_NoOps()
    {
        var userId = Guid.NewGuid();
        var user = SeedUser(userId);
        user.DisplayName = "Deleted User";
        // The minted tombstone email ApplyExpiredDeletionAnonymizationAsync writes — the
        // real post-erasure shape, not the user-editable DisplayName sentinel alone.
        user.Email = $"deleted-{userId:N}@deleted.local";
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        await _service.EnsureStubProfileAsync(userId, ct: TestContext.Current.CancellationToken, actorUserId: Guid.NewGuid());

        (await Db.Profiles.AnyAsync(p => p.UserId == userId, TestContext.Current.CancellationToken)).Should().BeFalse();
        AuditLog.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task AddUserEmailAsync_LegacyIdentityEmailWithoutRows_AddsVerifiedRow()
    {
        var userId = Guid.NewGuid();
        var user = SeedUser(userId);
        user.Email = "legacy@example.com";
        user.UserName = "legacy@example.com";
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await _service.AddUserEmailAsync(
            userId,
            new UserEmailAddCommand("legacy@example.com", IsVerified: true),
            TestContext.Current.CancellationToken);

        result.Added.Should().BeTrue();
        var row = await Db.UserEmails.AsNoTracking().SingleAsync(e => e.Id == result.EmailId, TestContext.Current.CancellationToken);
        row.UserId.Should().Be(userId);
        row.Email.Should().Be("legacy@example.com");
        row.IsVerified.Should().BeTrue();
    }

    [HumansTheory]
    [InlineData("deleted", false, false)]
    [InlineData("merged", true, false)]
    [InlineData("deleted", true, true)]
    [InlineData("merged", true, true)]
    [InlineData("legacy-deleted", false, false)]
    [InlineData("legacy-merged", true, false)]
    public async Task EmailInsertion_Tombstone_DoesNotRecreatePersonalData(string kind, bool verified, bool reconcile)
    {
        var userId = Guid.NewGuid();
        var user = SeedUser(userId);
        user.State = kind switch { "deleted" => UserState.Deleted, "merged" => UserState.Merged, _ => UserState.Bare };
        if (kind.Contains("deleted", StringComparison.Ordinal)) user.Email = $"deleted-{userId:N}@deleted.local";
        if (string.Equals(kind, "legacy-merged", StringComparison.Ordinal)) user.Email = $"merged-{userId:N}@merged.local";
        if (string.Equals(kind, "merged", StringComparison.Ordinal))
        {
            user.MergedAt = Clock.GetCurrentInstant();
            user.MergedToUserId = Guid.NewGuid();
        }
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        if (reconcile)
        {
            Func<Task> act = () => _service.ApplyUserEmailReconcilePlanAsync(userId,
                new UserEmailReconcilePlanCommand(null, null, null,
                    new UserEmail { Id = Guid.NewGuid(), UserId = userId, Email = "recreated@example.com", IsVerified = verified }),
                TestContext.Current.CancellationToken);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        else
        {
            var result = await _service.AddUserEmailAsync(userId,
                new UserEmailAddCommand("recreated@example.com", IsVerified: verified),
                TestContext.Current.CancellationToken);
            result.ErrorKey.Should().Be("Users_EmailUpdate_AccountUnavailable");
            result.Added.Should().BeFalse();
        }

        (await Db.UserEmails.CountAsync(TestContext.Current.CancellationToken)).Should().Be(0);
        AuditLog.ReceivedCalls().Should().BeEmpty();
    }

    [HumansFact]
    public async Task AddEmailAsync_OverlappingRequests_CreateOneRowAndOneVerificationToken()
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = Guid.NewGuid();
        SeedUser(userId);
        await Db.SaveChangesAsync(ct);
        var realRepository = new UserRepository(DbFactory, Clock);
        var repository = Substitute.For<IUserRepository>();
        repository.GetByIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(call => realRepository.GetByIdAsync(userId, call.Arg<CancellationToken>()));
        repository.GetUserEmailsByAddressAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(async call =>
            {
                // ILike needs PostgreSQL; perform the exact-address lookup over the same stored rows.
                await using var lookup = await DbFactory.CreateDbContextAsync(call.Arg<CancellationToken>());
                var rows = await lookup.UserEmails.AsNoTracking().ToListAsync(call.Arg<CancellationToken>());
                return (IReadOnlyList<UserEmail>)rows.Where(email => string.Equals(email.Email, call.ArgAt<string>(0), StringComparison.OrdinalIgnoreCase)
                    || string.Equals(email.Email, call.ArgAt<string?>(1), StringComparison.OrdinalIgnoreCase)).ToList();
            });
        repository.GetUserEmailsByUserIdForMutationAsync(userId, Arg.Any<CancellationToken>())
            .Returns(call => realRepository.GetUserEmailsByUserIdForMutationAsync(userId, call.Arg<CancellationToken>()));
        var firstInsertReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumeFirstInsert = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inserts = 0;
        repository.AddUserEmailAsync(Arg.Any<UserEmail>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            if (Interlocked.Increment(ref inserts) == 1)
            {
                firstInsertReached.TrySetResult();
                await resumeFirstInsert.Task.WaitAsync(ct);
            }
            await realRepository.AddUserEmailAsync(call.Arg<UserEmail>(), call.Arg<CancellationToken>());
        });
        var manager = Substitute.For<UserManager<User>>(
            Substitute.For<IUserStore<User>>(), null, null, null, null, null, null, null, null);
        manager.FindByIdAsync(userId.ToString()).Returns(new User { Id = userId });
        manager.GenerateUserTokenAsync(Arg.Any<User>(), Arg.Any<string>(), Arg.Any<string>()).Returns("verification-token");
        var services = new ServiceLocatorBuilder().With(Substitute.For<IAccountMergeService>()).Build();
        UserEmailService BuildEmailService() => new(
            repository, BuildWithUserRepository(repository), manager, Clock, AuditLog, services,
            NullLogger<UserEmailService>.Instance);

        var first = BuildEmailService().AddEmailAsync(userId, "same@example.com", ct);
        await firstInsertReached.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Task<AddEmailResult> second;
        try
        {
            second = BuildEmailService().AddEmailAsync(userId, "same@example.com", ct);
        }
        finally
        {
            resumeFirstInsert.TrySetResult();
        }
        var results = await Task.WhenAll(first, second);

        (await Db.UserEmails.AsNoTracking().Where(email => email.UserId == userId).ToListAsync(ct))
            .Should().ContainSingle();
        results.Should().ContainSingle(result => result.ErrorKey == null && result.Token == "verification-token");
        results.Should().ContainSingle(result => result.ErrorKey == "Users_Profile_EmailAlreadyAdded"
            && result.EmailId == Guid.Empty && result.Token == string.Empty);
        await manager.Received(1).GenerateUserTokenAsync(Arg.Any<User>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [HumansTheory]
    [InlineData("add")]
    [InlineData("verify")]
    [InlineData("remove")]
    public async Task EmailWrite_DisconnectAfterCommit_StillRepairsPrimary(string operation)
    {
        using var request = new CancellationTokenSource();
        var userId = Guid.NewGuid();
        var target = new UserEmail { Id = Guid.NewGuid(), UserId = userId, Email = "target@example.com" };
        var survivor = new UserEmail { Id = Guid.NewGuid(), UserId = userId, Email = "survivor@example.com", IsVerified = true };
        var rows = new List<UserEmail>();
        if (!string.Equals(operation, "add", StringComparison.Ordinal)) rows.Add(target);
        if (string.Equals(operation, "remove", StringComparison.Ordinal))
        {
            target.IsVerified = target.IsPrimary = true;
            rows.Add(survivor);
        }
        var repository = Substitute.For<IUserRepository>();
        repository.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(new User { Id = userId });
        repository.GetUserEmailsByAddressAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<UserEmail>>([]));
        repository.GetUserEmailByIdAndUserIdAsync(target.Id, userId, Arg.Any<CancellationToken>()).Returns(target);
        repository.GetUserEmailsByUserIdForMutationAsync(userId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<UserEmail>>(rows.ToList());
        });
        repository.AddUserEmailAsync(Arg.Any<UserEmail>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            call.Arg<CancellationToken>().Should().Be(request.Token);
            rows.Add(call.Arg<UserEmail>());
            await request.CancelAsync();
        });
        repository.UpdateUserEmailAsync(Arg.Any<UserEmail>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            call.Arg<CancellationToken>().Should().Be(request.Token);
            await request.CancelAsync();
        });
        repository.RemoveUserEmailAsync(Arg.Any<UserEmail>(), Arg.Any<CancellationToken>()).Returns(async call =>
        {
            call.Arg<CancellationToken>().Should().Be(request.Token);
            rows.Remove(call.Arg<UserEmail>());
            await request.CancelAsync();
        });
        repository.UpdateUserEmailsAsync(Arg.Any<IReadOnlyList<UserEmail>>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        var service = BuildWithUserRepository(repository);

        if (string.Equals(operation, "add", StringComparison.Ordinal))
            (await service.AddUserEmailAsync(userId, new UserEmailAddCommand(target.Email, IsVerified: true), request.Token))
                .Added.Should().BeTrue();
        else if (string.Equals(operation, "verify", StringComparison.Ordinal))
            (await service.UpdateUserEmailAsync(userId, target.Id, new UserEmailUpdateCommand(MarkVerified: true), request.Token))
                .Succeeded.Should().BeTrue();
        else
            (await service.RemoveUserEmailAsync(userId, target.Id, new UserEmailRemoveCommand(UserEmailRemovalMode.PlainEmail), request.Token))
                .Should().BeTrue();

        request.IsCancellationRequested.Should().BeTrue();
        var remaining = rows.Should().ContainSingle().Which;
        remaining.IsVerified.Should().BeTrue();
        remaining.IsPrimary.Should().BeTrue();
    }

    [HumansTheory]
    [InlineData(false, false, true)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    public async Task ReassignAsync_CleansFoldedProfilePictureWithoutTouchingSurvivor(
        bool versioned, bool cleanupFails, bool targetHasProfile)
    {
        var sourceUserId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var sourceProfileId = await SeedUserWithProfileAsync(sourceUserId);
        Guid? targetProfileId = targetHasProfile ? await SeedUserWithProfileAsync(targetUserId) : null;
        if (!targetHasProfile)
            SeedUser(targetUserId);
        var source = await Db.Profiles.SingleAsync(p => p.Id == sourceProfileId, TestContext.Current.CancellationToken);
        source.ProfilePictureContentType = "image/png";
        source.ProfilePictureStorageKey = versioned ? $"uploads/profile-pictures/{sourceProfileId}-old.png" : null;
        var previousKey = source.ProfilePictureStorageKey
            ?? ProfilePictureStorageKeys.ProfilePictureKey(sourceProfileId, "image/png");
        const string survivorKey = "uploads/profile-pictures/survivor.png";
        if (targetHasProfile)
        {
            var target = await Db.Profiles.SingleAsync(p => p.Id == targetProfileId, TestContext.Current.CancellationToken);
            target.ProfilePictureContentType = "image/png";
            target.ProfilePictureStorageKey = survivorKey;
        }
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        if (cleanupFails)
            _fileStorage.DeleteAsync(previousKey, CancellationToken.None)
                .Returns(Task.FromException(new IOException("Storage unavailable")));

        await _service.ReassignAsync(sourceUserId, targetUserId, Guid.NewGuid(), Clock.GetCurrentInstant(),
            TestContext.Current.CancellationToken);

        var freshSource = await Db.Profiles.AsNoTracking().SingleAsync(
            p => p.Id == sourceProfileId, TestContext.Current.CancellationToken);
        if (targetHasProfile)
        {
            freshSource.ProfilePictureContentType.Should().BeNull();
            freshSource.ProfilePictureStorageKey.Should().BeNull();
            await _fileStorage.Received(1).DeleteAsync(previousKey, CancellationToken.None);
            var target = await Db.Profiles.AsNoTracking().SingleAsync(
                p => p.Id == targetProfileId, TestContext.Current.CancellationToken);
            target.ProfilePictureStorageKey.Should().Be(survivorKey);
        }
        else
        {
            freshSource.ProfilePictureContentType.Should().Be("image/png");
            await _fileStorage.DidNotReceive().DeleteAsync(previousKey, Arg.Any<CancellationToken>());
        }
        await _fileStorage.DidNotReceive().DeleteAsync(survivorKey, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task AnonymizeProfileForDeletionAsync_ClearsProfilePicture()
    {
        var userId = Guid.NewGuid();
        var profileId = await SeedUserWithProfileAsync(userId);
        var profile = await Db.Profiles.FirstAsync(p => p.Id == profileId, TestContext.Current.CancellationToken);
        profile.ProfilePictureContentType = "image/png";
        profile.ProfilePictureStorageKey = "uploads/profile-pictures/versioned.png";
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var result = await _service.AnonymizeProfileForDeletionAsync(userId, TestContext.Current.CancellationToken);

        result.Anonymized.Should().BeTrue();

        var fresh = await Db.Profiles.AsNoTracking().SingleAsync(p => p.UserId == userId, TestContext.Current.CancellationToken);
        fresh.ProfilePictureContentType.Should().BeNull();
        fresh.ProfilePictureStorageKey.Should().BeNull();
        fresh.FirstName.Should().Be("Deleted");
        fresh.LastName.Should().Be("User");
    }

    private async Task<Guid> SeedUserWithProfileAsync(
        Guid userId,
        LegacyMembershipTier tier = LegacyMembershipTier.Volunteer,
        bool isApproved = false)
    {
        SeedUser(userId);
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BurnerName = "Test",
            FirstName = "First",
            LastName = "Last",
            MembershipTier = tier,
            IsApproved = isApproved,
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant(),
        };
        Db.Profiles.Add(profile);
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        return profile.Id;
    }

    private UserService BuildWithUserRepository(IUserRepository userRepository) =>
        new(
            userRepository,
            Substitute.For<ICommunicationPreferenceRepository>(),
            AdminAuthorization,
            Substitute.For<IFileStorage>(),
            Clock,
            NullLogger<UserService>.Instance, AuditLog);
}
