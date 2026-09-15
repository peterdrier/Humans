using Humans.AuditLog.Contracts;
using Humans.Auth.Contracts;
using AwesomeAssertions;
using Humans.Teams.Contracts;
using Humans.Users.Services;
using NodaTime;
using NSubstitute;
using Humans.Users.Contracts;

namespace Humans.Users.Tests.Services;

public sealed class DuplicateAccountServiceTests
{
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly IRoleAssignmentService _roles = Substitute.For<IRoleAssignmentService>();
    private readonly IAuditLogService _audit = Substitute.For<IAuditLogService>();
    private static readonly Instant Now = Instant.FromUtc(2026, 6, 6, 12, 0);

    private DuplicateAccountService Sut => new(_userService, _teamService, _roles, _audit);

    private void SetUsers(params UserInfo[] infos) =>
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyCollection<UserInfo>>(infos));

    private static UserEmail Email(Guid userId, string address, bool verified, bool primary = false) =>
        new() { Id = Guid.NewGuid(), UserId = userId, Email = address, IsVerified = verified, IsPrimary = primary };

    private static UserInfo MakeInfo(
        Guid userId, string? identityEmailColumn = null,
        Instant? mergedAt = null, Guid? mergedToUserId = null,
        params UserEmail[] emails)
    {
        var user = new User
        {
            Id = userId,
            DisplayName = "Test",
            PreferredLanguage = "en",
            CreatedAt = Now,
            Email = identityEmailColumn,
            MergedAt = mergedAt,
            MergedToUserId = mergedToUserId,
        };
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BurnerName = "Test",
            FirstName = "Test",
            LastName = "User",
            IsApproved = true,
            CreatedAt = Now,
            UpdatedAt = Now,
        };
        return UserInfoFactory.Create(user, emails, [], [], profile, [], [], [], []);
    }

    [HumansFact]
    public async Task DetectDuplicatesAsync_NeverSeesAMergeTombstone()
    {
        var survivor = Guid.NewGuid();
        SetUsers(MakeInfo(survivor, emails: [Email(survivor, "john@foo.com", verified: true, primary: true)]));

        var groups = await Sut.DetectDuplicatesAsync(Xunit.TestContext.Current.CancellationToken);

        groups.Should().BeEmpty(
            "a tombstone still carries its pre-merge User.Email column and would re-collide "
            + "with its own survivor forever; GetAllUserInfosAsync is one entry per living "
            + "human so the scan never sees one (pinned in CachingUserServiceTests)");
        await _userService.Received(1).GetAllUserInfosAsync(Arg.Any<CancellationToken>());
        await _userService.DidNotReceive().GetAllRawUserInfosAsync(Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task DetectDuplicatesAsync_FlagsTwoLiveAccountsSharingEmail()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        SetUsers(
            MakeInfo(a, emails: [Email(a, "dup@foo.com", verified: true, primary: true)]),
            MakeInfo(b, emails: [Email(b, "dup@foo.com", verified: true, primary: true)]));

        var groups = await Sut.DetectDuplicatesAsync(Xunit.TestContext.Current.CancellationToken);

        groups.Should().ContainSingle("two live accounts sharing a verified email is a real duplicate");
    }

    [HumansFact]
    public async Task DetectDuplicatesAsync_AuditsANewPairOnce_LowerIdIsTheEntity()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var (low, high) = string.CompareOrdinal(a.ToString(), b.ToString()) < 0 ? (a, b) : (b, a);
        SetUsers(
            MakeInfo(a, emails: [Email(a, "dup@foo.com", verified: true, primary: true)]),
            MakeInfo(b, emails: [Email(b, "dup@foo.com", verified: true, primary: true)]));

        await Sut.DetectDuplicatesAsync(Xunit.TestContext.Current.CancellationToken);

        await _audit.Received(1).LogAsync(
            AuditAction.DuplicateAccountFlagged, nameof(User), low, Arg.Any<string>(),
            DuplicateAccountService.ScanJobName, high, nameof(User));
    }

    [HumansFact]
    public async Task DetectDuplicatesAsync_DoesNotReauditAPairAlreadyFlagged()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        var (low, high) = string.CompareOrdinal(a.ToString(), b.ToString()) < 0 ? (a, b) : (b, a);
        SetUsers(
            MakeInfo(a, emails: [Email(a, "dup@foo.com", verified: true, primary: true)]),
            MakeInfo(b, emails: [Email(b, "dup@foo.com", verified: true, primary: true)]));
        _audit.GetFilteredEntriesAsync(nameof(User), low, null, Arg.Any<IReadOnlyList<AuditAction>>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<AuditLogEntrySnapshot>>(
            [
                new(Guid.NewGuid(), AuditAction.DuplicateAccountFlagged, nameof(User), low, "already flagged",
                    Now, null, high, nameof(User))
            ]));

        var groups = await Sut.DetectDuplicatesAsync(Xunit.TestContext.Current.CancellationToken);

        groups.Should().ContainSingle("the pair is still surfaced to the queue");
        await _audit.DidNotReceive().LogAsync(
            Arg.Any<AuditAction>(), Arg.Any<string>(), Arg.Any<Guid>(), Arg.Any<string>(),
            Arg.Any<string>(), Arg.Any<Guid?>(), Arg.Any<string?>());
    }
}
