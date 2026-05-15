using AwesomeAssertions;
using Humans.Application;
using Humans.Application.DTOs.EmailProblems;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Profiles;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;

namespace Humans.Application.Tests.Services;

public class EmailProblemsServiceTests
{
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly FakeClock _clock = new(Instant.FromUtc(2026, 5, 5, 12, 0));

    private readonly List<UserInfo> _allInfos = new();

    public EmailProblemsServiceTests()
    {
        _userService.GetAllUserInfos().Returns(_ => _allInfos.ToArray());
    }

    private EmailProblemsService Sut => new(
        _userEmailService, _userService, _clock);

    private static UserEmail Email(Guid userId, string address,
        bool isVerified = true, bool isPrimary = false, bool isGoogle = false) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = address,
            IsVerified = isVerified,
            IsPrimary = isPrimary,
            IsGoogle = isGoogle,
        };

    private static UserInfo MakeInfo(
        Guid userId,
        bool hasProfile = true,
        string? identityEmailColumn = null,
        params UserEmail[] emails)
    {
        var user = new User
        {
            Id = userId,
            DisplayName = "Test User",
            PreferredLanguage = "en",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            GoogleEmailStatus = GoogleEmailStatus.Unknown,
            Email = identityEmailColumn,
        };
        Profile? profile = hasProfile
            ? new Profile
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                BurnerName = "Test",
                FirstName = "Test",
                LastName = "User",
                IsApproved = true,
                CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
                UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            }
            : null;
        return UserInfo.Create(
            user: user,
            userEmails: emails,
            eventParticipations: Array.Empty<EventParticipation>(),
            externalLogins: Array.Empty<(string, string)>(),
            profile: profile,
            contactFields: Array.Empty<ContactField>(),
            profileLanguages: Array.Empty<ProfileLanguage>(),
            volunteerHistory: Array.Empty<VolunteerHistoryEntry>(),
            communicationPreferences: Array.Empty<CommunicationPreference>());
    }

    private void AddInfo(UserInfo info)
    {
        _allInfos.Add(info);
        _userService.GetUserInfoAsync(info.Id, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(info));
    }

    private void SetOrphans(params UserEmailOrphan[] orphans) =>
        _userEmailService.GetOrphanUserEmailsAsync(Arg.Any<CancellationToken>())
            .Returns(orphans);

    private void SetGhosts(params Guid[] ghostUserIds) =>
        _userService.GetUsersWithLoginsButNoEmailsAsync(Arg.Any<CancellationToken>())
            .Returns(ghostUserIds);

    [HumansFact]
    public async Task EmptySnapshot_ReturnsEmptyReport()
    {
        SetOrphans();
        SetGhosts();

        var report = await Sut.ScanAsync();

        report.Problems.Should().BeEmpty();
    }

    [HumansFact]
    public async Task DetectsMultipleIsPrimary()
    {
        var userId = Guid.NewGuid();
        AddInfo(MakeInfo(userId, emails: new[]
        {
            Email(userId, "a@x.com", isVerified: true, isPrimary: true),
            Email(userId, "b@x.com", isVerified: true, isPrimary: true),
        }));
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
        AddInfo(MakeInfo(userId, emails: new[]
        {
            Email(userId, "a@x.com", isVerified: true, isGoogle: true),
            Email(userId, "b@x.com", isVerified: true, isGoogle: true),
        }));
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
        AddInfo(MakeInfo(userId, emails: new[]
        {
            Email(userId, "a@x.com", isVerified: true),
            Email(userId, "b@x.com", isVerified: true),
        }));
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
        AddInfo(MakeInfo(userId, emails: new[]
        {
            Email(userId, "a@x.com", isVerified: false),
        }));
        SetOrphans();
        SetGhosts();

        var report = await Sut.ScanAsync();

        report.Problems.Should().NotContain(p => p.Kind == EmailProblemKind.ZeroIsPrimary);
    }

    [HumansFact]
    public async Task DetectsZeroIsGoogle()
    {
        var userId = Guid.NewGuid();
        AddInfo(MakeInfo(userId, emails: new[]
        {
            Email(userId, "a@x.com", isVerified: true, isPrimary: true),
        }));
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
        var unverified = Email(userId, "a@x.com", isVerified: false);
        AddInfo(MakeInfo(userId, emails: new[] { unverified }));
        SetOrphans();
        SetGhosts();

        var report = await Sut.ScanAsync();

        report.Problems.Should().ContainSingle(p =>
            p.Kind == EmailProblemKind.Unverified
            && p.UserId == userId
            && p.UserEmailId == unverified.Id
            && p.Email == "a@x.com");
    }

    [HumansFact]
    public async Task DetectsRawEmailCollisionAcrossUsers()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        AddInfo(MakeInfo(u1, emails: new[] { Email(u1, "joe@x.com", isVerified: true, isPrimary: true) }));
        AddInfo(MakeInfo(u2, emails: new[] { Email(u2, "joe@x.com", isVerified: true, isPrimary: true) }));
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
        AddInfo(MakeInfo(u1, emails: new[] { Email(u1, "joe@gmail.com", isVerified: true, isPrimary: true) }));
        AddInfo(MakeInfo(u2, emails: new[] { Email(u2, "joe@googlemail.com", isVerified: true, isPrimary: true) }));
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
        SetOrphans(new UserEmailOrphan(deadUserId, emailId, "ghost@x.com"));
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
        AddInfo(MakeInfo(u1, emails: new[] { Email(u1, "joe@x.com", isVerified: true, isPrimary: true) }));
        AddInfo(MakeInfo(u2, emails: new[] { Email(u2, "joe@x.com", isVerified: true, isPrimary: true) }));

        (await Sut.UsersShareAnyEmailAsync(u1, u2)).Should().BeTrue();
    }

    [HumansFact]
    public async Task UsersShareAnyEmail_GmailGooglemailEquivalent_True()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        AddInfo(MakeInfo(u1, emails: new[] { Email(u1, "joe@gmail.com", isVerified: true, isPrimary: true) }));
        AddInfo(MakeInfo(u2, emails: new[] { Email(u2, "joe@googlemail.com", isVerified: true, isPrimary: true) }));

        (await Sut.UsersShareAnyEmailAsync(u1, u2)).Should().BeTrue();
    }

    [HumansFact]
    public async Task UsersShareAnyEmail_NoOverlap_False()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        AddInfo(MakeInfo(u1, emails: new[] { Email(u1, "alice@x.com", isVerified: true, isPrimary: true) }));
        AddInfo(MakeInfo(u2, emails: new[] { Email(u2, "bob@x.com", isVerified: true, isPrimary: true) }));

        (await Sut.UsersShareAnyEmailAsync(u1, u2)).Should().BeFalse();
    }

    [HumansFact]
    public async Task UsersShareAnyEmail_SameUserId_False()
    {
        var u = Guid.NewGuid();
        AddInfo(MakeInfo(u, emails: new[] { Email(u, "joe@x.com", isVerified: true, isPrimary: true) }));

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

    [HumansFact]
    public async Task DetectsLegacyIdentityEmailNotInUserEmails()
    {
        var userId = Guid.NewGuid();
        AddInfo(MakeInfo(userId, identityEmailColumn: "legacy@x.com", emails: new[]
        {
            Email(userId, "other@x.com", isVerified: true, isPrimary: true),
        }));
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
        AddInfo(MakeInfo(userId, identityEmailColumn: "match@x.com", emails: new[]
        {
            Email(userId, "match@x.com", isVerified: true, isPrimary: true),
        }));
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
        AddInfo(MakeInfo(userId, identityEmailColumn: null));
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
        AddInfo(MakeInfo(userId, identityEmailColumn: "legacy@x.com", emails: new[]
        {
            Email(userId, "legacy@x.com", isVerified: false),
        }));
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
        // Issue nobodies-collective/Humans#697: the OAuth-aware "tag via
        // LinkAsync when an external login exists" branch is gone. The legacy
        // address is added as a plain verified row; the next OAuth sign-in's
        // reconcile attaches the provider tag via TagMoved.
        var userId = Guid.NewGuid();
        AddInfo(MakeInfo(userId, identityEmailColumn: "legacy@x.com"));

        var result = await Sut.BackfillLegacyIdentityEmailsAsync(Guid.NewGuid());

        result.Should().ContainSingle()
            .Which.Should().Be((userId, "legacy@x.com"));
        await _userEmailService.Received(1).AddVerifiedEmailAsync(
            userId, "legacy@x.com", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task BackfillLegacyIdentityEmails_AlreadyHasMatchingVerifiedRow_SkipsUser()
    {
        var userId = Guid.NewGuid();
        AddInfo(MakeInfo(userId, identityEmailColumn: "match@x.com", emails: new[]
        {
            Email(userId, "match@x.com", isVerified: true, isPrimary: true),
        }));

        var result = await Sut.BackfillLegacyIdentityEmailsAsync(Guid.NewGuid());

        result.Should().BeEmpty();
        await _userEmailService.DidNotReceive().AddVerifiedEmailAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task BackfillLegacyIdentityEmails_NullColumn_SkipsUser()
    {
        var userId = Guid.NewGuid();
        AddInfo(MakeInfo(userId, identityEmailColumn: null));

        var result = await Sut.BackfillLegacyIdentityEmailsAsync(Guid.NewGuid());

        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task DoesNotFlagLegacyEmail_WhenProfileLessUserHasMatchingVerifiedRow()
    {
        var userId = Guid.NewGuid();
        AddInfo(MakeInfo(userId, hasProfile: false, identityEmailColumn: "import@x.com", emails: new[]
        {
            Email(userId, "import@x.com", isVerified: true),
        }));
        SetOrphans();
        SetGhosts();

        var report = await Sut.ScanAsync();

        report.Problems.Should().NotContain(p =>
            p.Kind == EmailProblemKind.LegacyIdentityEmailNotInUserEmails);
    }

    [HumansFact]
    public async Task DetectsLegacyEmail_WhenProfileLessUserHasNoMatchingRow()
    {
        var userId = Guid.NewGuid();
        AddInfo(MakeInfo(userId, hasProfile: false, identityEmailColumn: "legacy@x.com"));
        SetOrphans();
        SetGhosts();

        var report = await Sut.ScanAsync();

        report.Problems.Should().ContainSingle(p =>
            p.Kind == EmailProblemKind.LegacyIdentityEmailNotInUserEmails
            && p.UserId == userId
            && p.Email == "legacy@x.com");
    }

    [HumansFact]
    public async Task BackfillLegacyIdentityEmails_ProfileLessUserAlreadyMatched_SkipsUser()
    {
        var userId = Guid.NewGuid();
        AddInfo(MakeInfo(userId, hasProfile: false, identityEmailColumn: "import@x.com", emails: new[]
        {
            Email(userId, "import@x.com", isVerified: true),
        }));

        var result = await Sut.BackfillLegacyIdentityEmailsAsync(Guid.NewGuid());

        result.Should().BeEmpty();
        await _userEmailService.DidNotReceive().AddVerifiedEmailAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
