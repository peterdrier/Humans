using AwesomeAssertions;
using Humans.Application.Interfaces.Gdpr;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Users;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Repositories.Profiles;
using Humans.Infrastructure.Repositories.Users;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services.Users;

public sealed class UserServiceProfileOnboardingMutationTests : ServiceTestHarness
{
    private readonly UserService _service;

    public UserServiceProfileOnboardingMutationTests()
    {
        var userRepository = new UserRepository(DbFactory, Clock);
        var communicationPreferenceRepository = Substitute.For<ICommunicationPreferenceRepository>();

        _service = new UserService(
            userRepository,
            communicationPreferenceRepository,
            AdminAuthorization,
            Clock,
            NullLogger<UserService>.Instance);
    }

    [HumansFact]
    public async Task ContributeForUserAsync_EmitsUserProfileSlices_WithOAuthKeySourcedFromProviderColumn()
    {
        // The JSON key stays "IsOAuth" per memory/code/no-rename-serialized-fields.md
        // (exports are JSON files users download). The value sources from
        // (Provider != null), not IsGoogle.
        var userId = Guid.NewGuid();
        SeedUser(userId);
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
        await Db.SaveChangesAsync();

        var slices = await _service.ContributeForUserAsync(userId, CancellationToken.None);

        slices.Select(s => s.SectionName).Should().Contain([
            GdprExportSections.Account,
            GdprExportSections.EventParticipations,
            GdprExportSections.Profile,
            GdprExportSections.ContactFields,
            GdprExportSections.UserEmails,
            GdprExportSections.VolunteerHistory,
            GdprExportSections.Languages,
            GdprExportSections.CommunicationPreferences
        ]);
        var userEmailsSlice = slices.Single(s =>
            string.Equals(s.SectionName, GdprExportSections.UserEmails, StringComparison.Ordinal));
        var json = System.Text.Json.JsonSerializer.Serialize(userEmailsSlice.Data);
        json.Should().Contain("\"IsOAuth\":true");
        json.Should().Contain("\"IsNotificationTarget\":true");
        json.Should().NotContain("\"IsPrimary\":");

        var accountSlice = slices.Single(s =>
            string.Equals(s.SectionName, GdprExportSections.Account, StringComparison.Ordinal));
        var accountJson = System.Text.Json.JsonSerializer.Serialize(accountSlice.Data);
        accountJson.Should().Contain("\"Email\":\"g@example.com\"");
    }

    [HumansFact]
    public async Task GetByIdAsync_HydratesUserEmailsThroughUserRepository()
    {
        var userId = Guid.NewGuid();
        SeedUser(userId, "Hydrated User");
        var email = new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = "hydrated@example.com",
            IsVerified = true,
            IsPrimary = true,
        };
        Db.UserEmails.Add(email);
        await Db.SaveChangesAsync();

        var user = await _service.GetByIdAsync(userId);

        user.Should().NotBeNull();
        user!.UserEmails.Should().ContainSingle(e =>
            e.Id == email.Id && e.Email == "hydrated@example.com");
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
        await Db.SaveChangesAsync();

        var result = await _service.GetUsersWithLoginsButNoEmailsAsync();

        result.Should().Equal([userWithoutEmail.Id]);
    }

    [HumansFact]
    public async Task PurgeOwnDataAsync_RemovesUserEmailsThroughUserRepository()
    {
        var user = SeedUser(Guid.NewGuid(), "Purge Me");
        Db.UserEmails.Add(new UserEmail
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Email = "purge-me@example.com",
            IsVerified = true,
            IsPrimary = true,
        });
        await Db.SaveChangesAsync();

        var displayName = await _service.PurgeOwnDataAsync(user.Id);

        displayName.Should().Be("Purge Me");
        var remaining = await Db.UserEmails.AsNoTracking()
            .Where(e => e.UserId == user.Id)
            .ToListAsync();
        remaining.Should().BeEmpty();
    }

    [HumansFact]
    public async Task ApplyProfileOnboardingMutationAsync_RecordConsentCheckCleared_FlipsIsApprovedAndStamps()
    {
        var userId = Guid.NewGuid();
        var reviewerId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);

        var result = await _service.ApplyProfileOnboardingMutationAsync(
            userId,
            new UserProfileOnboardingCommand(
                UserProfileOnboardingMutation.RecordConsentCheck,
                ActorUserId: reviewerId,
                ConsentCheckStatus: ConsentCheckStatus.Cleared,
                Notes: "Looks good"));

        result.Success.Should().BeTrue();
        var profile = await Db.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId);
        profile.ConsentCheckStatus.Should().Be(ConsentCheckStatus.Cleared);
        profile.IsApproved.Should().BeTrue();
        profile.ConsentCheckAt.Should().Be(Clock.GetCurrentInstant());
        profile.ConsentCheckedByUserId.Should().Be(reviewerId);
        profile.ConsentCheckNotes.Should().Be("Looks good");
    }

    [HumansFact]
    public async Task ApplyProfileOnboardingMutationAsync_RecordConsentCheckFlagged_KeepsIsApprovedFalse()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);

        var result = await _service.ApplyProfileOnboardingMutationAsync(
            userId,
            new UserProfileOnboardingCommand(
                UserProfileOnboardingMutation.RecordConsentCheck,
                ActorUserId: Guid.NewGuid(),
                ConsentCheckStatus: ConsentCheckStatus.Flagged,
                Notes: "Concern X"));

        result.Success.Should().BeTrue();
        var profile = await Db.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId);
        profile.ConsentCheckStatus.Should().Be(ConsentCheckStatus.Flagged);
        profile.IsApproved.Should().BeFalse();
    }

    [HumansFact]
    public async Task ApplyProfileOnboardingMutationAsync_RecordConsentCheckAlreadyRejected_BlocksClear()
    {
        var userId = Guid.NewGuid();
        var profileId = await SeedUserWithProfileAsync(userId);
        var profile = await Db.Profiles.FirstAsync(p => p.Id == profileId);
        profile.RejectedAt = Clock.GetCurrentInstant();
        await Db.SaveChangesAsync();

        var result = await _service.ApplyProfileOnboardingMutationAsync(
            userId,
            new UserProfileOnboardingCommand(
                UserProfileOnboardingMutation.RecordConsentCheck,
                ActorUserId: Guid.NewGuid(),
                ConsentCheckStatus: ConsentCheckStatus.Cleared));

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("AlreadyRejected");
    }

    [HumansFact]
    public async Task ApplyProfileOnboardingMutationAsync_RecordConsentCheckPending_Throws()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);

        await Assert.ThrowsAsync<ArgumentException>(() =>
            _service.ApplyProfileOnboardingMutationAsync(
                userId,
                new UserProfileOnboardingCommand(
                    UserProfileOnboardingMutation.RecordConsentCheck,
                    ActorUserId: Guid.NewGuid(),
                    ConsentCheckStatus: ConsentCheckStatus.Pending)));
    }

    [HumansFact]
    public async Task ApplyProfileOnboardingMutationAsync_RejectSignup_StampsRejectionFields()
    {
        var userId = Guid.NewGuid();
        var reviewerId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);

        var result = await _service.ApplyProfileOnboardingMutationAsync(
            userId,
            new UserProfileOnboardingCommand(
                UserProfileOnboardingMutation.RejectSignup,
                ActorUserId: reviewerId,
                RejectionReason: "Spam account"));

        result.Success.Should().BeTrue();
        var profile = await Db.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId);
        profile.RejectedAt.Should().Be(Clock.GetCurrentInstant());
        profile.RejectedByUserId.Should().Be(reviewerId);
        profile.RejectionReason.Should().Be("Spam account");
        profile.IsApproved.Should().BeFalse();
    }

    [HumansFact]
    public async Task ApplyProfileOnboardingMutationAsync_RejectSignupAlreadyRejected_ReturnsErrorKey()
    {
        var userId = Guid.NewGuid();
        var profileId = await SeedUserWithProfileAsync(userId);
        var profile = await Db.Profiles.FirstAsync(p => p.Id == profileId);
        profile.RejectedAt = Clock.GetCurrentInstant();
        await Db.SaveChangesAsync();

        var result = await _service.ApplyProfileOnboardingMutationAsync(
            userId,
            new UserProfileOnboardingCommand(
                UserProfileOnboardingMutation.RejectSignup,
                ActorUserId: Guid.NewGuid()));

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("AlreadyRejected");
    }

    [HumansFact]
    public async Task ApplyProfileOnboardingMutationAsync_ApproveVolunteer_FlipsIsApprovedTrue()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId, isApproved: false);

        var result = await _service.ApplyProfileOnboardingMutationAsync(
            userId,
            new UserProfileOnboardingCommand(UserProfileOnboardingMutation.ApproveVolunteer));

        result.Success.Should().BeTrue();
        var profile = await Db.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId);
        profile.IsApproved.Should().BeTrue();
        profile.UpdatedAt.Should().Be(Clock.GetCurrentInstant());
    }

    [HumansFact]
    public async Task ApplyProfileOnboardingMutationAsync_SetSuspensionTrue_SetsSuspendedAndState()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);

        var result = await _service.ApplyProfileOnboardingMutationAsync(
            userId,
            new UserProfileOnboardingCommand(
                UserProfileOnboardingMutation.SetSuspension,
                Suspended: true,
                Notes: "Disruptive"));

        result.Success.Should().BeTrue();
        var profile = await Db.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId);
#pragma warning disable HUM_PROFILE_ISSUSPENDED
        profile.IsSuspended.Should().BeTrue();
#pragma warning restore HUM_PROFILE_ISSUSPENDED
        profile.State.Should().Be(ProfileState.Suspended);
        profile.AdminNotes.Should().Be("Disruptive");
    }

    [HumansFact]
    public async Task ApplyProfileOnboardingMutationAsync_SetSuspensionFalse_RestoresActiveWhenIdentityComplete()
    {
        var userId = Guid.NewGuid();
        var profileId = await SeedUserWithProfileAsync(userId);
        var profile = await Db.Profiles.FirstAsync(p => p.Id == profileId);
#pragma warning disable HUM_PROFILE_ISSUSPENDED
        profile.IsSuspended = true;
#pragma warning restore HUM_PROFILE_ISSUSPENDED
        profile.State = ProfileState.Suspended;
        await Db.SaveChangesAsync();

        var result = await _service.ApplyProfileOnboardingMutationAsync(
            userId,
            new UserProfileOnboardingCommand(
                UserProfileOnboardingMutation.SetSuspension,
                Suspended: false));

        result.Success.Should().BeTrue();
        var fresh = await Db.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId);
#pragma warning disable HUM_PROFILE_ISSUSPENDED
        fresh.IsSuspended.Should().BeFalse();
#pragma warning restore HUM_PROFILE_ISSUSPENDED
        fresh.State.Should().Be(ProfileState.Active);
    }

    [HumansFact]
    public async Task ApplyProfileOnboardingMutationAsync_SetConsentCheckPending_FlipsToPending()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);

        var result = await _service.ApplyProfileOnboardingMutationAsync(
            userId,
            new UserProfileOnboardingCommand(UserProfileOnboardingMutation.SetConsentCheckPending));

        result.Success.Should().BeTrue();
        var profile = await Db.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId);
        profile.ConsentCheckStatus.Should().Be(ConsentCheckStatus.Pending);
    }

    [HumansFact]
    public async Task ApplyProfileOnboardingMutationAsync_NoProfile_ReturnsNotFound()
    {
        var result = await _service.ApplyProfileOnboardingMutationAsync(
            Guid.NewGuid(),
            new UserProfileOnboardingCommand(UserProfileOnboardingMutation.ApproveVolunteer));

        result.Success.Should().BeFalse();
        result.ErrorKey.Should().Be("NotFound");
    }

    [HumansFact(Timeout = 5000)]
    public async Task EnsureStubProfileAsync_TwoConcurrentCallers_OnlyOneAddAsync()
    {
        var userId = Guid.NewGuid();
        SeedUser(userId);
        await Db.SaveChangesAsync();

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
            Task.Run(() => service.EnsureStubProfileAsync(userId)),
            Task.Run(() => service.EnsureStubProfileAsync(userId)));
#pragma warning restore VSTHRD003

        await fakeRepo.Received(1).AddAsync(Arg.Any<Profile>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task EnsureStubProfileAsync_WithSeededNames_CreatesActiveProfileWithTrimmedNames()
    {
        var userId = Guid.NewGuid();
        SeedUser(userId);
        await Db.SaveChangesAsync();

        await _service.EnsureStubProfileAsync(userId, " Sparkle ", " Ada ", " Lovelace ");

        var profile = await Db.Profiles.SingleAsync(p => p.UserId == userId);
        profile.BurnerName.Should().Be("Sparkle");
        profile.FirstName.Should().Be("Ada");
        profile.LastName.Should().Be("Lovelace");
        profile.State.Should().Be(ProfileState.Active);
    }

    [HumansFact]
    public async Task EnsureStubProfileAsync_WithoutNames_CreatesStubProfile()
    {
        var userId = Guid.NewGuid();
        SeedUser(userId);
        await Db.SaveChangesAsync();

        await _service.EnsureStubProfileAsync(userId);

        var profile = await Db.Profiles.SingleAsync(p => p.UserId == userId);
        profile.BurnerName.Should().BeEmpty();
        profile.State.Should().Be(ProfileState.Stub);
    }

    [HumansFact]
    public async Task EnsureStubProfileAsync_MergedTombstone_NoOps()
    {
        var userId = Guid.NewGuid();
        var user = SeedUser(userId);
        user.MergedAt = Clock.GetCurrentInstant();
        await Db.SaveChangesAsync();

        await _service.EnsureStubProfileAsync(userId);

        (await Db.Profiles.AnyAsync(p => p.UserId == userId)).Should().BeFalse();
    }

    [HumansFact]
    public async Task EnsureStubProfileAsync_DeletedTombstone_NoOps()
    {
        var userId = Guid.NewGuid();
        var user = SeedUser(userId);
        user.DisplayName = "Deleted User";
        await Db.SaveChangesAsync();

        await _service.EnsureStubProfileAsync(userId);

        (await Db.Profiles.AnyAsync(p => p.UserId == userId)).Should().BeFalse();
    }

    [HumansFact]
    public async Task AddUserEmailAsync_LegacyIdentityEmailWithoutRows_AddsVerifiedRow()
    {
        var userId = Guid.NewGuid();
        var user = SeedUser(userId);
        user.Email = "legacy@example.com";
        user.UserName = "legacy@example.com";
        await Db.SaveChangesAsync();

        var result = await _service.AddUserEmailAsync(
            userId,
            new UserEmailAddCommand("legacy@example.com", IsVerified: true),
            CancellationToken.None);

        result.Added.Should().BeTrue();
        var row = await Db.UserEmails.AsNoTracking().SingleAsync(e => e.Id == result.EmailId);
        row.UserId.Should().Be(userId);
        row.Email.Should().Be("legacy@example.com");
        row.IsVerified.Should().BeTrue();
    }

    [HumansFact]
    public async Task SetMembershipTierAsync_UpdatesTierAndUpdatedAt()
    {
        var userId = Guid.NewGuid();
        await SeedUserWithProfileAsync(userId);
        Clock.Advance(NodaTime.Duration.FromHours(1));

        await _service.SetMembershipTierAsync(userId, MembershipTier.Colaborador);

        var profile = await Db.Profiles.AsNoTracking().FirstAsync(p => p.UserId == userId);
        profile.MembershipTier.Should().Be(MembershipTier.Colaborador);
        profile.UpdatedAt.Should().Be(Clock.GetCurrentInstant());
    }

    [HumansFact]
    public async Task SetMembershipTierAsync_UnknownUser_NoOps()
    {
        var unknownUserId = Guid.NewGuid();

        var updated = await _service.SetMembershipTierAsync(unknownUserId, MembershipTier.Asociado);

        updated.Should().BeFalse();
        var profileExists = await Db.Profiles.AsNoTracking().AnyAsync(p => p.UserId == unknownUserId);
        profileExists.Should().BeFalse();
    }

    [HumansFact]
    public async Task AnonymizeProfileForDeletionAsync_ClearsProfileAndReturnsPreviousPictureMetadata()
    {
        var userId = Guid.NewGuid();
        var profileId = await SeedUserWithProfileAsync(userId);
        var profile = await Db.Profiles.FirstAsync(p => p.Id == profileId);
        profile.ProfilePictureContentType = "image/png";
        await Db.SaveChangesAsync();

        var result = await _service.AnonymizeProfileForDeletionAsync(userId);

        result.Anonymized.Should().BeTrue();
        result.ProfileId.Should().Be(profileId);
        result.PreviousProfilePictureContentType.Should().Be("image/png");

        var fresh = await Db.Profiles.AsNoTracking().SingleAsync(p => p.UserId == userId);
        fresh.ProfilePictureContentType.Should().BeNull();
        fresh.FirstName.Should().Be("Deleted");
        fresh.LastName.Should().Be("User");
    }

    private async Task<Guid> SeedUserWithProfileAsync(
        Guid userId,
        MembershipTier tier = MembershipTier.Volunteer,
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
        await Db.SaveChangesAsync();
        return profile.Id;
    }

    private UserService BuildWithUserRepository(IUserRepository userRepository) =>
        new(
            userRepository,
            Substitute.For<ICommunicationPreferenceRepository>(),
            AdminAuthorization,
            Clock,
            NullLogger<UserService>.Instance);
}
