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

    private void SetProfiles(params FullProfile[] profiles) =>
        _profileService.GetFullProfileSnapshotAsync(Arg.Any<CancellationToken>())
            .Returns(profiles);

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
}
