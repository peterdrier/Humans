using AwesomeAssertions;
using Humans.AuditLog.Contracts;
using Humans.Users.Contracts;
using Humans.Users.Tests.Infrastructure;
using NodaTime;
using NSubstitute;
using Humans.Users.Services;

namespace Humans.Users.Tests.Services;

public sealed class EmailProblemsServiceTests : ServiceTestHarness
{
    private readonly IUserEmailService _userEmailService = Substitute.For<IUserEmailService>();
    private readonly IUserManagement _userService = Substitute.For<IUserManagement>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();

    private readonly List<UserInfo> _allInfos = [];

    public EmailProblemsServiceTests()
        : base(Instant.FromUtc(2026, 5, 5, 12, 0))
    {
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult<IReadOnlyCollection<UserInfo>>(_allInfos.ToArray()));
    }

    private EmailProblemsService Sut => new(
        _userEmailService, _userService, _audit, Clock);

    private static UserEmail Email(Guid userId, string address,
        bool isVerified = true, bool isPrimary = false) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Email = address,
            IsVerified = isVerified,
            IsPrimary = isPrimary,
        };

    private static Profile MakeProfile(Guid userId) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BurnerName = "Test",
            FirstName = "Test",
            LastName = "User",
            IsApproved = true,
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
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
        return UserInfoFactory.Create(
            user: user,
            userEmails: emails,
            eventParticipations: [],
            externalLogins: [],
            profile: profile,
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: [],
            communicationPreferences: []);
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
    public async Task ScanAsync_ExcludesPerUserHygieneProblemsForTombstones()
    {
        // Per-user hygiene problems must not be reported for merge-source / GDPR-deleted tombstones:
        // their scrubbed sentinel emails (…@merged.local / …@deleted.local) yield only false
        // ZeroIsPrimary / Unverified positives.
        var liveId = Guid.NewGuid();
        AddInfo(MakeInfo(liveId, emails:
        [
            Email(liveId, "live@x.com", isVerified: true) // real ZeroIsPrimary problem
        ]));

        var mergedId = Guid.NewGuid();
        var mergedUser = new User
        {
            Id = mergedId,
            DisplayName = "Test User",
            PreferredLanguage = "en",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            MergedAt = Instant.FromUtc(2026, 2, 1, 0, 0),
            MergedToUserId = Guid.NewGuid(),
        };
        AddInfo(mergedUser.ToUserInfo(
            userEmails: [Email(mergedId, "merged-1@merged.local", isVerified: false)],
            profile: MakeProfile(mergedId)));

        var deletedId = Guid.NewGuid();
        var deletedUser = new User
        {
            Id = deletedId,
            DisplayName = "Deleted User",
            PreferredLanguage = "en",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            // The minted tombstone email is what marks IsTombstone; the DisplayName sentinel alone
            // is user-editable and not enough.
            Email = $"deleted-{deletedId:N}@deleted.local",
        };
        AddInfo(deletedUser.ToUserInfo(
            userEmails: [Email(deletedId, "deleted-1@deleted.local", isVerified: false)],
            profile: MakeProfile(deletedId)));

        SetOrphans();
        SetGhosts();

        var report = await Sut.ScanAsync(Xunit.TestContext.Current.CancellationToken);

        report.Problems.Should().NotContain(p => p.UserId == mergedId);
        report.Problems.Should().NotContain(p => p.UserId == deletedId);
        // The live account's hygiene problem is untouched.
        report.Problems.Should().Contain(p =>
            p.Kind == EmailProblemKind.ZeroIsPrimary && p.UserId == liveId);
    }

    [HumansFact]
    public async Task ScanAsync_OrphanAndGhostRowsForTombstones_ArePreserved()
    {
        // A UserEmail row or external login still pointing at a tombstone is real leftover data,
        // and this page is the only place an admin can clean it up: the tombstone suppression
        // covers per-user hygiene noise only.
        var mergedId = Guid.NewGuid();
        var mergedUser = new User
        {
            Id = mergedId,
            DisplayName = "Test User",
            PreferredLanguage = "en",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            MergedAt = Instant.FromUtc(2026, 2, 1, 0, 0),
            MergedToUserId = Guid.NewGuid(),
        };
        AddInfo(mergedUser.ToUserInfo(profile: MakeProfile(mergedId)));

        var ghostId = Guid.NewGuid();
        var ghostUser = new User
        {
            Id = ghostId,
            DisplayName = "Test User",
            PreferredLanguage = "en",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            MergedAt = Instant.FromUtc(2026, 2, 1, 0, 0),
            MergedToUserId = Guid.NewGuid(),
        };
        AddInfo(ghostUser.ToUserInfo());

        var orphanEmailId = Guid.NewGuid();
        SetOrphans(new UserEmailOrphan(mergedId, orphanEmailId, "leftover@merged.local"));
        SetGhosts(ghostId);

        var report = await Sut.ScanAsync(Xunit.TestContext.Current.CancellationToken);

        report.Problems.Should().Contain(p =>
            p.Kind == EmailProblemKind.OrphanUserEmail
            && p.UserId == mergedId
            && p.UserEmailId == orphanEmailId);
        report.Problems.Should().Contain(p =>
            p.Kind == EmailProblemKind.GhostExternalLogins && p.UserId == ghostId);
    }

    [HumansFact]
    public async Task EmptySnapshot_ReturnsEmptyReport()
    {
        SetOrphans();
        SetGhosts();

        var report = await Sut.ScanAsync(Xunit.TestContext.Current.CancellationToken);

        report.Problems.Should().BeEmpty();
    }

    [HumansFact]
    public async Task DetectsMultipleIsPrimary()
    {
        var userId = Guid.NewGuid();
        AddInfo(MakeInfo(userId, emails:
        [
            Email(userId, "a@x.com", isVerified: true, isPrimary: true),
            Email(userId, "b@x.com", isVerified: true, isPrimary: true)
        ]));
        SetOrphans();
        SetGhosts();

        var report = await Sut.ScanAsync(Xunit.TestContext.Current.CancellationToken);

        report.Problems.Should().ContainSingle(p =>
            p.Kind == EmailProblemKind.MultipleIsPrimary && p.UserId == userId);
    }

    [HumansFact]
    public async Task DetectsZeroIsPrimary_WhenUserHasVerifiedEmails()
    {
        var userId = Guid.NewGuid();
        AddInfo(MakeInfo(userId, emails:
        [
            Email(userId, "a@x.com", isVerified: true),
            Email(userId, "b@x.com", isVerified: true)
        ]));
        SetOrphans();
        SetGhosts();

        var report = await Sut.ScanAsync(Xunit.TestContext.Current.CancellationToken);

        report.Problems.Should().ContainSingle(p =>
            p.Kind == EmailProblemKind.ZeroIsPrimary && p.UserId == userId);
    }

    [HumansFact]
    public async Task DoesNotFlagZeroIsPrimary_WhenUserHasNoVerifiedEmails()
    {
        var userId = Guid.NewGuid();
        AddInfo(MakeInfo(userId, emails:
        [
            Email(userId, "a@x.com", isVerified: false)
        ]));
        SetOrphans();
        SetGhosts();

        var report = await Sut.ScanAsync(Xunit.TestContext.Current.CancellationToken);

        report.Problems.Should().NotContain(p => p.Kind == EmailProblemKind.ZeroIsPrimary);
    }

    [HumansFact]
    public async Task DetectsUnverifiedEmail_RegardlessOfFlags()
    {
        var userId = Guid.NewGuid();
        var unverified = Email(userId, "a@x.com", isVerified: false);
        AddInfo(MakeInfo(userId, emails: [unverified]));
        SetOrphans();
        SetGhosts();

        var report = await Sut.ScanAsync(Xunit.TestContext.Current.CancellationToken);

        report.Problems.Should().ContainSingle(p =>
            p.Kind == EmailProblemKind.Unverified
            && p.UserId == userId
            && p.UserEmailId == unverified.Id
            && p.Email == "a@x.com");
    }

    [HumansFact]
    public async Task DetectsOrphanUserEmail()
    {
        var deadUserId = Guid.NewGuid();
        var emailId = Guid.NewGuid();
        SetOrphans(new UserEmailOrphan(deadUserId, emailId, "ghost@x.com"));
        SetGhosts();

        var report = await Sut.ScanAsync(Xunit.TestContext.Current.CancellationToken);

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

        var report = await Sut.ScanAsync(Xunit.TestContext.Current.CancellationToken);

        report.Problems.Should().ContainSingle(p =>
            p.Kind == EmailProblemKind.GhostExternalLogins && p.UserId == ghostUserId);
    }

    [HumansFact]
    public async Task DetectsLegacyIdentityEmailNotInUserEmails()
    {
        var userId = Guid.NewGuid();
        AddInfo(MakeInfo(userId, identityEmailColumn: "legacy@x.com", emails:
        [
            Email(userId, "other@x.com", isVerified: true, isPrimary: true)
        ]));
        SetOrphans();
        SetGhosts();

        var report = await Sut.ScanAsync(Xunit.TestContext.Current.CancellationToken);

        report.Problems.Should().ContainSingle(p =>
            p.Kind == EmailProblemKind.LegacyIdentityEmailNotInUserEmails
            && p.UserId == userId
            && p.Email == "legacy@x.com");
    }

    [HumansFact]
    public async Task DoesNotFlagLegacyEmail_WhenMatchingVerifiedRowExists()
    {
        var userId = Guid.NewGuid();
        AddInfo(MakeInfo(userId, identityEmailColumn: "match@x.com", emails:
        [
            Email(userId, "match@x.com", isVerified: true, isPrimary: true)
        ]));
        SetOrphans();
        SetGhosts();

        var report = await Sut.ScanAsync(Xunit.TestContext.Current.CancellationToken);

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

        var report = await Sut.ScanAsync(Xunit.TestContext.Current.CancellationToken);

        report.Problems.Should().NotContain(p =>
            p.Kind == EmailProblemKind.LegacyIdentityEmailNotInUserEmails);
    }

    [HumansFact]
    public async Task DoesNotFlagLegacyEmail_WhenMatchingRowIsUnverified()
    {
        var userId = Guid.NewGuid();
        AddInfo(MakeInfo(userId, identityEmailColumn: "legacy@x.com", emails:
        [
            Email(userId, "legacy@x.com", isVerified: false)
        ]));
        SetOrphans();
        SetGhosts();

        var report = await Sut.ScanAsync(Xunit.TestContext.Current.CancellationToken);

        report.Problems.Should().ContainSingle(p =>
            p.Kind == EmailProblemKind.LegacyIdentityEmailNotInUserEmails
            && p.UserId == userId);
    }

    [HumansFact]
    public async Task BackfillLegacyIdentityEmails_FlaggedUser_CallsAddVerifiedAndReturnsPair()
    {
        // The legacy address is added as a plain verified row; the next OAuth sign-in's
        // reconcile attaches the provider tag via TagMoved.
        var userId = Guid.NewGuid();
        AddInfo(MakeInfo(userId, identityEmailColumn: "legacy@x.com"));

        var result = await Sut.BackfillLegacyIdentityEmailsAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        result.Should().ContainSingle()
            .Which.Should().Be((userId, "legacy@x.com"));
        await _userEmailService.Received(1).AddVerifiedEmailAsync(
            userId, "legacy@x.com", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task BackfillLegacyIdentityEmails_AlreadyHasMatchingVerifiedRow_SkipsUser()
    {
        var userId = Guid.NewGuid();
        AddInfo(MakeInfo(userId, identityEmailColumn: "match@x.com", emails:
        [
            Email(userId, "match@x.com", isVerified: true, isPrimary: true)
        ]));

        var result = await Sut.BackfillLegacyIdentityEmailsAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        result.Should().BeEmpty();
        await _userEmailService.DidNotReceive().AddVerifiedEmailAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task BackfillLegacyIdentityEmails_NullColumn_SkipsUser()
    {
        var userId = Guid.NewGuid();
        AddInfo(MakeInfo(userId, identityEmailColumn: null));

        var result = await Sut.BackfillLegacyIdentityEmailsAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task DoesNotFlagLegacyEmail_WhenProfileLessUserHasMatchingVerifiedRow()
    {
        var userId = Guid.NewGuid();
        AddInfo(MakeInfo(userId, hasProfile: false, identityEmailColumn: "import@x.com", emails:
        [
            Email(userId, "import@x.com", isVerified: true)
        ]));
        SetOrphans();
        SetGhosts();

        var report = await Sut.ScanAsync(Xunit.TestContext.Current.CancellationToken);

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

        var report = await Sut.ScanAsync(Xunit.TestContext.Current.CancellationToken);

        report.Problems.Should().ContainSingle(p =>
            p.Kind == EmailProblemKind.LegacyIdentityEmailNotInUserEmails
            && p.UserId == userId
            && p.Email == "legacy@x.com");
    }

    [HumansFact]
    public async Task BackfillLegacyIdentityEmails_ProfileLessUserAlreadyMatched_SkipsUser()
    {
        var userId = Guid.NewGuid();
        AddInfo(MakeInfo(userId, hasProfile: false, identityEmailColumn: "import@x.com", emails:
        [
            Email(userId, "import@x.com", isVerified: true)
        ]));

        var result = await Sut.BackfillLegacyIdentityEmailsAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        result.Should().BeEmpty();
        await _userEmailService.DidNotReceive().AddVerifiedEmailAsync(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task BackfillLegacyIdentityEmails_AuditsEachCreatedRowAgainstTheActor()
    {
        var actorId = Guid.NewGuid();
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        AddInfo(MakeInfo(u1, identityEmailColumn: "a@x.com"));
        AddInfo(MakeInfo(u2, identityEmailColumn: "b@x.com"));

        await Sut.BackfillLegacyIdentityEmailsAsync(actorId, Xunit.TestContext.Current.CancellationToken);

        await _audit.Received(1).LogAsync(
            AuditAction.LegacyIdentityEmailBackfilled, nameof(User), u1,
            Arg.Is<string>(s => s.Contains("a@x.com")), actorId);
        await _audit.Received(1).LogAsync(
            AuditAction.LegacyIdentityEmailBackfilled, nameof(User), u2,
            Arg.Is<string>(s => s.Contains("b@x.com")), actorId);
    }

    [HumansFact]
    public async Task BackfillLegacyIdentityEmails_NothingToBackfill_WritesNoAudit()
    {
        var userId = Guid.NewGuid();
        AddInfo(MakeInfo(userId, identityEmailColumn: null));

        await Sut.BackfillLegacyIdentityEmailsAsync(Guid.NewGuid(), Xunit.TestContext.Current.CancellationToken);

        await _audit.DidNotReceive().LogAsync(
            Arg.Any<AuditAction>(), Arg.Any<string>(), Arg.Any<Guid>(),
            Arg.Any<string>(), Arg.Any<Guid>());
    }
}
