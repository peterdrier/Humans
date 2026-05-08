using AwesomeAssertions;
using Humans.Application.DTOs.EmailProblems;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Profile;
using Humans.Domain.Entities;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.Services;

public class EmailProblemsServiceTests
{
    private readonly IProfileService _profileService = Substitute.For<IProfileService>();
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly FakeClock _clock = new(NodaTime.Instant.FromUtc(2026, 5, 5, 12, 0));

    private EmailProblemsService Sut => new(
        _profileService, _userEmailService, _userService, _clock);

    private static FullProfile MakeProfile(Guid userId, params UserEmailSnapshot[] emails) =>
        new FullProfile(
            UserId: userId, DisplayName: "Test User", ProfilePictureUrl: null,
            HasCustomPicture: false, ProfileId: Guid.NewGuid(), UpdatedAtTicks: 0,
            BurnerName: "Test", Bio: null, Pronouns: null, ContributionInterests: null,
            City: null, CountryCode: null, Latitude: null, Longitude: null,
            BirthdayDay: null, BirthdayMonth: null,
            IsApproved: true, IsSuspended: false,
            CVEntries: Array.Empty<CVEntry>(),
            UserEmails: emails);

    private void SetProfiles(params FullProfile[] profiles)
    {
        var users = profiles
            .Select(p => new User { Id = p.UserId })
            .ToList();
        _userService.GetAllUsersAsync(Arg.Any<CancellationToken>())
            .Returns(users);

        foreach (var p in profiles)
        {
            _profileService.GetFullProfileAsync(p.UserId, Arg.Any<CancellationToken>())
                .Returns(new ValueTask<FullProfile?>(p));
        }
    }

    private void SetOrphans(params UserEmail[] orphans) =>
        _userEmailService.GetOrphanUserEmailsAsync(Arg.Any<CancellationToken>())
            .Returns(orphans);

    private void SetGhosts(params Guid[] ghostUserIds) =>
        _userService.GetUsersWithLoginsButNoEmailsAsync(Arg.Any<CancellationToken>())
            .Returns(ghostUserIds);

    [HumansFact]
    public async Task EmptySnapshot_ReturnsEmptyReport()
    {
        SetProfiles();
        SetOrphans();
        SetGhosts();

        var report = await Sut.ScanAsync();

        report.Problems.Should().BeEmpty();
    }

    [HumansFact]
    public async Task DetectsMultipleIsPrimary()
    {
        var userId = Guid.NewGuid();
        SetProfiles(MakeProfile(userId,
            new UserEmailSnapshot(Guid.NewGuid(), "a@x.com", true, true, false),
            new UserEmailSnapshot(Guid.NewGuid(), "b@x.com", true, true, false)));
        SetOrphans();
        SetGhosts();

        var report = await Sut.ScanAsync();

        report.Problems.Should().ContainSingle(p =>
            p.Kind == EmailProblemKind.MultipleIsPrimary && p.UserId == userId);
    }

    [HumansFact]
    public async Task DetectsMultipleIsGoogle()
    {
        var userId = Guid.NewGuid();
        SetProfiles(MakeProfile(userId,
            new UserEmailSnapshot(Guid.NewGuid(), "a@x.com", true, false, true),
            new UserEmailSnapshot(Guid.NewGuid(), "b@x.com", true, false, true)));
        SetOrphans();
        SetGhosts();

        var report = await Sut.ScanAsync();

        report.Problems.Should().ContainSingle(p =>
            p.Kind == EmailProblemKind.MultipleIsGoogle && p.UserId == userId);
    }

    [HumansFact]
    public async Task DetectsZeroIsPrimary_WhenUserHasVerifiedEmails()
    {
        var userId = Guid.NewGuid();
        SetProfiles(MakeProfile(userId,
            new UserEmailSnapshot(Guid.NewGuid(), "a@x.com", true, false, false),
            new UserEmailSnapshot(Guid.NewGuid(), "b@x.com", true, false, false)));
        SetOrphans();
        SetGhosts();

        var report = await Sut.ScanAsync();

        report.Problems.Should().ContainSingle(p =>
            p.Kind == EmailProblemKind.ZeroIsPrimary && p.UserId == userId);
    }

    [HumansFact]
    public async Task DoesNotFlagZeroIsPrimary_WhenUserHasNoVerifiedEmails()
    {
        var userId = Guid.NewGuid();
        SetProfiles(MakeProfile(userId,
            new UserEmailSnapshot(Guid.NewGuid(), "a@x.com", false, false, false)));
        SetOrphans();
        SetGhosts();

        var report = await Sut.ScanAsync();

        report.Problems.Should().NotContain(p => p.Kind == EmailProblemKind.ZeroIsPrimary);
    }

    [HumansFact]
    public async Task DetectsZeroIsGoogle()
    {
        var userId = Guid.NewGuid();
        SetProfiles(MakeProfile(userId,
            new UserEmailSnapshot(Guid.NewGuid(), "a@x.com", true, true, false)));
        SetOrphans();
        SetGhosts();

        var report = await Sut.ScanAsync();

        report.Problems.Should().ContainSingle(p =>
            p.Kind == EmailProblemKind.ZeroIsGoogle && p.UserId == userId);
    }

    [HumansFact]
    public async Task DetectsUnverifiedEmail_RegardlessOfFlags()
    {
        var userId = Guid.NewGuid();
        var emailId = Guid.NewGuid();
        SetProfiles(MakeProfile(userId,
            new UserEmailSnapshot(emailId, "a@x.com", false, false, false)));
        SetOrphans();
        SetGhosts();

        var report = await Sut.ScanAsync();

        report.Problems.Should().ContainSingle(p =>
            p.Kind == EmailProblemKind.Unverified
            && p.UserId == userId
            && p.UserEmailId == emailId
            && p.Email == "a@x.com");
    }

    [HumansFact]
    public async Task DetectsRawEmailCollisionAcrossUsers()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        SetProfiles(
            MakeProfile(u1, new UserEmailSnapshot(Guid.NewGuid(), "joe@x.com", true, true, false)),
            MakeProfile(u2, new UserEmailSnapshot(Guid.NewGuid(), "joe@x.com", true, true, false)));
        SetOrphans();
        SetGhosts();

        var report = await Sut.ScanAsync();

        report.Problems.Should().ContainSingle(p =>
            p.Kind == EmailProblemKind.SharedAcrossUsers
            && p.Email == "joe@x.com"
            && (p.UserId == u1 || p.UserId == u2)
            && (p.OtherUserId == u1 || p.OtherUserId == u2)
            && p.UserId != p.OtherUserId);
    }

    [HumansFact]
    public async Task DetectsNormalizationEquivalentCollision()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        SetProfiles(
            MakeProfile(u1, new UserEmailSnapshot(Guid.NewGuid(), "joe@gmail.com", true, true, false)),
            MakeProfile(u2, new UserEmailSnapshot(Guid.NewGuid(), "joe@googlemail.com", true, true, false)));
        SetOrphans();
        SetGhosts();

        var report = await Sut.ScanAsync();

        report.Problems.Should().ContainSingle(p => p.Kind == EmailProblemKind.SharedAcrossUsers);
    }

    [HumansFact]
    public async Task DetectsOrphanUserEmail()
    {
        var deadUserId = Guid.NewGuid();
        var emailId = Guid.NewGuid();
        SetProfiles();
        SetOrphans(new UserEmail
        {
            Id = emailId,
            UserId = deadUserId,
            Email = "ghost@x.com",
            IsVerified = true
        });
        SetGhosts();

        var report = await Sut.ScanAsync();

        report.Problems.Should().ContainSingle(p =>
            p.Kind == EmailProblemKind.OrphanUserEmail
            && p.UserEmailId == emailId
            && p.UserId == deadUserId
            && p.Email == "ghost@x.com");
    }

    [HumansFact]
    public async Task DetectsGhostExternalLogins()
    {
        var ghostUserId = Guid.NewGuid();
        SetProfiles();
        SetOrphans();
        SetGhosts(ghostUserId);

        var report = await Sut.ScanAsync();

        report.Problems.Should().ContainSingle(p =>
            p.Kind == EmailProblemKind.GhostExternalLogins && p.UserId == ghostUserId);
    }

    [HumansFact]
    public async Task UsersShareAnyEmail_ExactMatch_True()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        SetProfiles(
            MakeProfile(u1, new UserEmailSnapshot(Guid.NewGuid(), "joe@x.com", true, true, false)),
            MakeProfile(u2, new UserEmailSnapshot(Guid.NewGuid(), "joe@x.com", true, true, false)));

        (await Sut.UsersShareAnyEmailAsync(u1, u2)).Should().BeTrue();
    }

    [HumansFact]
    public async Task UsersShareAnyEmail_GmailGooglemailEquivalent_True()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        SetProfiles(
            MakeProfile(u1, new UserEmailSnapshot(Guid.NewGuid(), "joe@gmail.com", true, true, false)),
            MakeProfile(u2, new UserEmailSnapshot(Guid.NewGuid(), "joe@googlemail.com", true, true, false)));

        (await Sut.UsersShareAnyEmailAsync(u1, u2)).Should().BeTrue();
    }

    [HumansFact]
    public async Task UsersShareAnyEmail_NoOverlap_False()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        SetProfiles(
            MakeProfile(u1, new UserEmailSnapshot(Guid.NewGuid(), "alice@x.com", true, true, false)),
            MakeProfile(u2, new UserEmailSnapshot(Guid.NewGuid(), "bob@x.com", true, true, false)));

        (await Sut.UsersShareAnyEmailAsync(u1, u2)).Should().BeFalse();
    }

    [HumansFact]
    public async Task UsersShareAnyEmail_SameUserId_False()
    {
        var u = Guid.NewGuid();
        SetProfiles(MakeProfile(u, new UserEmailSnapshot(Guid.NewGuid(), "joe@x.com", true, true, false)));

        (await Sut.UsersShareAnyEmailAsync(u, u)).Should().BeFalse();
    }

    [HumansFact]
    public async Task IsGhostExternalLoginsUser_InSet_True()
    {
        var ghostUserId = Guid.NewGuid();
        SetGhosts(ghostUserId);

        (await Sut.IsGhostExternalLoginsUserAsync(ghostUserId)).Should().BeTrue();
    }

    [HumansFact]
    public async Task IsGhostExternalLoginsUser_NotInSet_False()
    {
        var ghostUserId = Guid.NewGuid();
        var otherUserId = Guid.NewGuid();
        SetGhosts(ghostUserId);

        (await Sut.IsGhostExternalLoginsUserAsync(otherUserId)).Should().BeFalse();
    }

    private void SetUsersWithProfiles(params (User User, FullProfile Profile)[] pairs)
    {
        var users = pairs.Select(p => p.User).ToList();
        _userService.GetAllUsersAsync(Arg.Any<CancellationToken>()).Returns(users);
        foreach (var (u, profile) in pairs)
        {
            _profileService.GetFullProfileAsync(u.Id, Arg.Any<CancellationToken>())
                .Returns(new ValueTask<FullProfile?>(profile));
        }
    }

    private static User MakeUser(Guid id, string? legacyEmail) =>
        new User { Id = id, DisplayName = "Test User", Email = legacyEmail };

    [HumansFact]
    public async Task DetectsLegacyIdentityEmailNotInUserEmails()
    {
        var userId = Guid.NewGuid();
        var user = MakeUser(userId, "legacy@x.com");
        SetUsersWithProfiles((user, MakeProfile(userId,
            new UserEmailSnapshot(Guid.NewGuid(), "other@x.com", true, true, false))));
        SetOrphans();
        SetGhosts();

        var report = await Sut.ScanAsync();

        report.Problems.Should().ContainSingle(p =>
            p.Kind == EmailProblemKind.LegacyIdentityEmailNotInUserEmails
            && p.UserId == userId
            && p.Email == "legacy@x.com");
    }

    [HumansFact]
    public async Task DoesNotFlagLegacyEmail_WhenMatchingVerifiedRowExists()
    {
        var userId = Guid.NewGuid();
        var user = MakeUser(userId, "match@x.com");
        SetUsersWithProfiles((user, MakeProfile(userId,
            new UserEmailSnapshot(Guid.NewGuid(), "match@x.com", true, true, false))));
        SetOrphans();
        SetGhosts();

        var report = await Sut.ScanAsync();

        report.Problems.Should().NotContain(p =>
            p.Kind == EmailProblemKind.LegacyIdentityEmailNotInUserEmails);
    }

    [HumansFact]
    public async Task DoesNotFlagLegacyEmail_WhenColumnIsNull()
    {
        var userId = Guid.NewGuid();
        var user = MakeUser(userId, legacyEmail: null);
        SetUsersWithProfiles((user, MakeProfile(userId)));
        SetOrphans();
        SetGhosts();

        var report = await Sut.ScanAsync();

        report.Problems.Should().NotContain(p =>
            p.Kind == EmailProblemKind.LegacyIdentityEmailNotInUserEmails);
    }

    [HumansFact]
    public async Task DoesNotFlagLegacyEmail_WhenMatchingRowIsUnverified()
    {
        var userId = Guid.NewGuid();
        var user = MakeUser(userId, "legacy@x.com");
        SetUsersWithProfiles((user, MakeProfile(userId,
            new UserEmailSnapshot(Guid.NewGuid(), "legacy@x.com", false, false, false))));
        SetOrphans();
        SetGhosts();

        var report = await Sut.ScanAsync();

        report.Problems.Should().ContainSingle(p =>
            p.Kind == EmailProblemKind.LegacyIdentityEmailNotInUserEmails
            && p.UserId == userId);
    }

    [HumansFact]
    public async Task BackfillLegacyIdentityEmails_FlaggedUser_CallsAddVerifiedAndReturnsPair()
    {
        var userId = Guid.NewGuid();
        var user = MakeUser(userId, "legacy@x.com");
        SetUsersWithProfiles((user, MakeProfile(userId)));

        var result = await Sut.BackfillLegacyIdentityEmailsAsync();

        result.Should().ContainSingle()
            .Which.Should().Be((userId, "legacy@x.com"));
        await _userEmailService.Received(1).AddVerifiedEmailAsync(
            userId, "legacy@x.com", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task BackfillLegacyIdentityEmails_AlreadyHasMatchingVerifiedRow_SkipsUser()
    {
        var userId = Guid.NewGuid();
        var user = MakeUser(userId, "match@x.com");
        SetUsersWithProfiles((user, MakeProfile(userId,
            new UserEmailSnapshot(Guid.NewGuid(), "match@x.com", true, true, false))));

        var result = await Sut.BackfillLegacyIdentityEmailsAsync();

        result.Should().BeEmpty();
        await _userEmailService.DidNotReceive().AddVerifiedEmailAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task BackfillLegacyIdentityEmails_NullColumn_SkipsUser()
    {
        var userId = Guid.NewGuid();
        var user = MakeUser(userId, legacyEmail: null);
        SetUsersWithProfiles((user, MakeProfile(userId)));

        var result = await Sut.BackfillLegacyIdentityEmailsAsync();

        result.Should().BeEmpty();
    }
}
