using AwesomeAssertions;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using NodaTime;
using NSubstitute;

namespace Humans.Application.Tests.Services;

public sealed class DuplicateAccountServiceTests
{
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly ITeamService _teamService = Substitute.For<ITeamService>();
    private readonly IRoleAssignmentService _roles = Substitute.For<IRoleAssignmentService>();
    private static readonly Instant Now = Instant.FromUtc(2026, 6, 6, 12, 0);

    private DuplicateAccountService Sut => new(_userService, _teamService, _roles);

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
            GoogleEmailStatus = GoogleEmailStatus.Unknown,
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
        return UserInfo.Create(user, emails, [], [], profile, [], [], [], []);
    }

    [HumansFact]
    public async Task DetectDuplicatesAsync_ExcludesMergeTombstone()
    {
        var survivor = Guid.NewGuid();
        var tombstone = Guid.NewGuid();
        SetUsers(
            MakeInfo(survivor, emails: [Email(survivor, "john@foo.com", verified: true, primary: true)]),
            // Tombstone: its UserEmail rows were reassigned to the survivor, but the legacy
            // User.Email column lingers — UserInfo.Email falls back to it. Must NOT re-collide.
            MakeInfo(tombstone, identityEmailColumn: "john@foo.com", mergedAt: Now, mergedToUserId: survivor));

        var groups = await Sut.DetectDuplicatesAsync();

        groups.Should().BeEmpty("a merge tombstone must not re-collide with its own survivor");
    }

    [HumansFact]
    public async Task DetectDuplicatesAsync_FlagsTwoLiveAccountsSharingEmail()
    {
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        SetUsers(
            MakeInfo(a, emails: [Email(a, "dup@foo.com", verified: true, primary: true)]),
            MakeInfo(b, emails: [Email(b, "dup@foo.com", verified: true, primary: true)]));

        var groups = await Sut.DetectDuplicatesAsync();

        groups.Should().ContainSingle("two live accounts sharing a verified email is a real duplicate");
    }
}
