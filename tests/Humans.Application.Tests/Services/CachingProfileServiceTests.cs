using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Humans.Application;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Application.Interfaces.Caching;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Infrastructure.Services.Profiles;
using NodaTime;
using NSubstitute;
using Xunit;

namespace Humans.Application.Tests.Services;

public class CachingProfileServiceTests
{
    // CachingProfileService (Phase 5.5) is Singleton and resolves Scoped deps
    // (inner IProfileService keyed "profile-inner", IUserService,
    // INavBadgeCacheInvalidator, INotificationMeterCacheInvalidator) per-call
    // via IServiceScopeFactory.
    //
    // Test strategy: build a real ServiceCollection with NSubstitute substitutes
    // registered for the Scoped deps under the same keys used in production, then
    // build a ServiceProvider from which CachingProfileService can resolve them via
    // scope. This avoids scope-factory mocking boilerplate while keeping tests concise.

    private readonly IProfileService _inner = Substitute.For<IProfileService>();
    private readonly IProfileRepository _profileRepository = Substitute.For<IProfileRepository>();
    private readonly IUserService _userService = Substitute.For<IUserService>();
    private readonly IUserEmailRepository _userEmailRepository = Substitute.For<IUserEmailRepository>();
    private readonly INavBadgeCacheInvalidator _navBadge = Substitute.For<INavBadgeCacheInvalidator>();
    private readonly INotificationMeterCacheInvalidator _notificationMeter = Substitute.For<INotificationMeterCacheInvalidator>();

    private CachingProfileService CreateSut()
    {
        var services = new ServiceCollection();
        // Register the inner service under the same keyed key used in production
        services.AddKeyedScoped<IProfileService>(CachingProfileService.InnerServiceKey, (_, _) => _inner);
        services.AddScoped(_ => _userService);
        services.AddScoped(_ => _navBadge);
        services.AddScoped(_ => _notificationMeter);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new CachingProfileService(_profileRepository, _userEmailRepository, scopeFactory);
    }

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
    public async Task SaveProfileAsync_RefreshesDictEntry_InsteadOfEviction()
    {
        var userId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var profile = new Profile { Id = profileId, UserId = userId, BurnerName = "After save" };
        var user = new User { Id = userId, DisplayName = "Name" };

        _profileRepository.GetByUserIdReadOnlyAsync(userId, Arg.Any<CancellationToken>())
            .Returns(profile);
        _userService.GetByIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(user);
        _userEmailRepository.GetByUserIdReadOnlyAsync(userId, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<UserEmail>());

        var sut = CreateSut();

        // Preload dict with a stale entry via a prior GetFullProfileAsync
        var stale = SampleFullProfile(userId) with { BurnerName = "Before save" };
        _inner.GetFullProfileAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<FullProfile?>(stale));
        await sut.GetFullProfileAsync(userId);

        // Perform the write — RefreshEntryAsync should reload from repositories.
        await sut.SaveProfileAsync(userId, "Name", SampleSaveRequest(), "en");

        // The next read should return the fresh value synchronously from the dict
        // (not delegate to _inner — _inner.GetFullProfileAsync was only called
        //  during the pre-write prime).
        var fresh = await sut.GetFullProfileAsync(userId);
        fresh.Should().NotBeNull();
        fresh!.BurnerName.Should().Be("After save");
        await _inner.Received(1).GetFullProfileAsync(userId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvalidateAsync_DeletedProfile_RemovesEntryFromDict()
    {
        var userId = Guid.NewGuid();

        // _profileRepository is NOT called during GetFullProfileAsync (the dict prime uses _inner).
        // The first call to _profileRepository happens inside RefreshEntryAsync (called by
        // InvalidateCacheAsync). Returning null there simulates a deleted profile.
        _profileRepository.GetByUserIdReadOnlyAsync(userId, Arg.Any<CancellationToken>())
            .Returns((Profile?)null);

        var sut = CreateSut();

        // Populate dict via _inner (does not touch _profileRepository)
        _inner.GetFullProfileAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<FullProfile?>(SampleFullProfile(userId)));
        await sut.GetFullProfileAsync(userId);

        // InvalidateCacheAsync triggers RefreshEntryAsync; profile is null → entry removed
        await sut.InvalidateCacheAsync(userId);

        // Next read: dict miss (entry was removed); _inner also returns null
        _inner.GetFullProfileAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<FullProfile?>((FullProfile?)null));
        var result = await sut.GetFullProfileAsync(userId);
        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveCVEntriesAsync_RefreshesDictEntry()
    {
        var userId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var profile = new Profile { Id = profileId, UserId = userId, BurnerName = "Same burner" };
        profile.VolunteerHistory.Add(new VolunteerHistoryEntry
        {
            Id = Guid.NewGuid(), ProfileId = profileId,
            Date = new LocalDate(2025, 3, 1), EventName = "Nowhere 2025", Description = "Sound crew",
        });
        var user = new User { Id = userId, DisplayName = "Name" };

        _profileRepository.GetByUserIdReadOnlyAsync(userId, Arg.Any<CancellationToken>()).Returns(profile);
        _userService.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);
        _userEmailRepository.GetByUserIdReadOnlyAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmail>());

        var sut = CreateSut();

        // Prime the dict with a stale entry that has no CV entries
        var stale = SampleFullProfile(userId) with { CVEntries = Array.Empty<CVEntry>() };
        _inner.GetFullProfileAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<FullProfile?>(stale));
        await sut.GetFullProfileAsync(userId);

        // Call the write — decorator should refresh dict
        await sut.SaveCVEntriesAsync(userId,
            new[] { new CVEntry(new LocalDate(2025, 3, 1), "Nowhere 2025", "Sound crew") });

        // Next read must return the fresh FullProfile from the dict (has CVEntries)
        var fresh = await sut.GetFullProfileAsync(userId);
        fresh.Should().NotBeNull();
        fresh!.CVEntries.Should().ContainSingle(e => e.EventName == "Nowhere 2025");
        await _inner.Received(1).GetFullProfileAsync(userId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvalidateAsync_ExistingUser_ReloadsEntry()
    {
        var userId = Guid.NewGuid();
        var profile = new Profile { Id = Guid.NewGuid(), UserId = userId, BurnerName = "After" };
        var user = new User { Id = userId, DisplayName = "Name" };

        _profileRepository.GetByUserIdReadOnlyAsync(userId, Arg.Any<CancellationToken>())
            .Returns(profile);
        _userService.GetByIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(user);
        _userEmailRepository.GetByUserIdReadOnlyAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmail>());

        var sut = CreateSut();

        // Prime with a stale entry
        var stale = SampleFullProfile(userId) with { BurnerName = "Before" };
        _inner.GetFullProfileAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<FullProfile?>(stale));
        await sut.GetFullProfileAsync(userId);

        // Invalidate via the new interface
        await ((IFullProfileInvalidator)sut).InvalidateAsync(userId);

        // Next read returns the fresh value from dict
        var fresh = await sut.GetFullProfileAsync(userId);
        fresh!.BurnerName.Should().Be("After");
        await _inner.Received(1).GetFullProfileAsync(userId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task InvalidateAsync_DeletedUser_RemovesEntry()
    {
        var userId = Guid.NewGuid();
        _profileRepository.GetByUserIdReadOnlyAsync(userId, Arg.Any<CancellationToken>())
            .Returns((Profile?)null);

        var sut = CreateSut();

        // Prime the dict
        _inner.GetFullProfileAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<FullProfile?>(SampleFullProfile(userId)));
        await sut.GetFullProfileAsync(userId);

        // Invalidate — repo returns null, so entry is removed
        await ((IFullProfileInvalidator)sut).InvalidateAsync(userId);

        // Next read sees empty dict, delegates to inner
        _inner.GetFullProfileAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<FullProfile?>((FullProfile?)null));
        var result = await sut.GetFullProfileAsync(userId);
        result.Should().BeNull();
        await _inner.Received(2).GetFullProfileAsync(userId, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task SaveProfileLanguagesAsync_RefreshesDictEntry_WhenCached()
    {
        var userId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var seededAt = SystemClock.Instance.GetCurrentInstant();
        var profile = new Profile { Id = profileId, UserId = userId, UpdatedAt = seededAt };
        var user = new User { Id = userId, DisplayName = "Name" };

        _profileRepository.GetByUserIdReadOnlyAsync(userId, Arg.Any<CancellationToken>()).Returns(profile);
        _userService.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(user);
        _userEmailRepository.GetByUserIdReadOnlyAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new List<UserEmail>());

        var sut = CreateSut();

        // Prime dict with a stale FullProfile carrying the correct profileId
        // (so the O(n) scan inside SaveProfileLanguagesAsync can resolve userId)
        var stale = SampleFullProfile(userId) with { ProfileId = profileId, UpdatedAtTicks = 0 };
        _inner.GetFullProfileAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<FullProfile?>(stale));
        await sut.GetFullProfileAsync(userId);

        // Save languages (takes profileId)
        await sut.SaveProfileLanguagesAsync(profileId, Array.Empty<ProfileLanguage>());

        // Refresh should have run — dict entry has a non-zero UpdatedAtTicks now
        var fresh = await sut.GetFullProfileAsync(userId);
        fresh.Should().NotBeNull();
        fresh!.UpdatedAtTicks.Should().Be(seededAt.ToUnixTimeTicks());
        await _inner.Received(1).GetFullProfileAsync(userId, Arg.Any<CancellationToken>());
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
