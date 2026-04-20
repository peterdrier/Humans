using AwesomeAssertions;
using Humans.Application;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Repositories;
using Humans.Infrastructure.Services.Profiles;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services;

public class CachingProfileServiceTests
{
    private readonly IProfileService _inner = Substitute.For<IProfileService>();
    private readonly IProfileRepository _profileRepository = Substitute.For<IProfileRepository>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IUserEmailRepository _userEmailRepository = Substitute.For<IUserEmailRepository>();
    private readonly INavBadgeCacheInvalidator _navBadge = Substitute.For<INavBadgeCacheInvalidator>();
    private readonly INotificationMeterCacheInvalidator _notificationMeter = Substitute.For<INotificationMeterCacheInvalidator>();

    private CachingProfileService CreateSut() => new(
        _inner, _profileRepository, _userService, _userEmailRepository, _navBadge, _notificationMeter);

    [Fact]
    public async Task GetFullProfileAsync_DictMiss_DelegatesToInnerAndPopulatesDict()
    {
        var userId = Guid.NewGuid();
        var fullProfile = SampleFullProfile(userId);
        _inner.GetFullProfileAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<FullProfile?>(fullProfile));

        var sut = CreateSut();

        var first = await sut.GetFullProfileAsync(userId);
        first.Should().BeSameAs(fullProfile);
        await _inner.Received(1).GetFullProfileAsync(userId, Arg.Any<CancellationToken>());

        var second = await sut.GetFullProfileAsync(userId);
        second.Should().BeSameAs(fullProfile);
        await _inner.Received(1).GetFullProfileAsync(userId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task GetFullProfileAsync_DictMissReturnsNull_DoesNotPopulateDict()
    {
        var userId = Guid.NewGuid();
        _inner.GetFullProfileAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<FullProfile?>((FullProfile?)null));

        var sut = CreateSut();

        var first = await sut.GetFullProfileAsync(userId);
        first.Should().BeNull();

        var second = await sut.GetFullProfileAsync(userId);
        second.Should().BeNull();
        await _inner.Received(2).GetFullProfileAsync(userId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveProfileAsync_EvictsDict_NextGetGoesToInner()
    {
        var userId = Guid.NewGuid();
        var profile = SampleFullProfile(userId);
        _inner.GetFullProfileAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<FullProfile?>(profile));

        var sut = CreateSut();

        // Populate dict
        await sut.GetFullProfileAsync(userId);
        await _inner.Received(1).GetFullProfileAsync(userId, Arg.Any<CancellationToken>());

        // A write evicts the dict entry
        await sut.SaveProfileAsync(userId, "Name", SampleSaveRequest(), "en");

        // Next read hits inner again
        await sut.GetFullProfileAsync(userId);
        await _inner.Received(2).GetFullProfileAsync(userId, Arg.Any<CancellationToken>());
    }

    private static ProfileSaveRequest SampleSaveRequest() => new(
        BurnerName: "Burner", FirstName: "First", LastName: "Last",
        City: null, CountryCode: null, Latitude: null, Longitude: null, PlaceId: null,
        Bio: null, Pronouns: null, ContributionInterests: null, BoardNotes: null,
        BirthdayMonth: null, BirthdayDay: null,
        EmergencyContactName: null, EmergencyContactPhone: null, EmergencyContactRelationship: null,
        NoPriorBurnExperience: false,
        ProfilePictureData: null, ProfilePictureContentType: null, RemoveProfilePicture: false,
        SelectedTier: null, ApplicationMotivation: null, ApplicationAdditionalInfo: null,
        ApplicationSignificantContribution: null, ApplicationRoleUnderstanding: null);

    private static FullProfile SampleFullProfile(Guid userId) => new(
        UserId: userId, DisplayName: "Name", ProfilePictureUrl: null,
        HasCustomPicture: false, ProfileId: Guid.NewGuid(), UpdatedAtTicks: 0,
        BurnerName: null, Bio: null, Pronouns: null, ContributionInterests: null,
        City: null, CountryCode: null, Latitude: null, Longitude: null,
        BirthdayDay: null, BirthdayMonth: null,
        IsApproved: true, IsSuspended: false,
        CVEntries: Array.Empty<CVEntry>(),
        NotificationEmail: null);
}
