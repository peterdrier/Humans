using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Xunit;
using Humans.Application;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Services.Users;

namespace Humans.Application.Tests.Services.Users;

/// <summary>
/// Issue #703. Tests pinning the CachingUserService decorator contract: dict
/// hit on cold/warm, write-then-read consistency, concurrent reads, and that
/// each of the 8 contributing tables flows through to UserInfo.
/// </summary>
public class CachingUserServiceTests
{
    private readonly IUserService _inner = Substitute.For<IUserService>();
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly IUserEmailRepository _userEmailRepo = Substitute.For<IUserEmailRepository>();
    private readonly IProfileRepository _profileRepo = Substitute.For<IProfileRepository>();
    private readonly IContactFieldRepository _contactFieldRepo = Substitute.For<IContactFieldRepository>();

    private CachingUserService CreateSut()
    {
        var services = new ServiceCollection();
        services.AddKeyedScoped<IUserService>(CachingUserService.InnerServiceKey, (_, _) => _inner);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new CachingUserService(
            _userRepo, _userEmailRepo, _profileRepo, _contactFieldRepo,
            scopeFactory, NullLogger<CachingUserService>.Instance);
    }

    private static User SampleUser(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        DisplayName = "Alice",
        PreferredLanguage = "en",
        CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
    };

    private static UserInfo SampleUserInfo(Guid userId, string displayName = "Alice") =>
        UserInfo.Create(
            new User { Id = userId, DisplayName = displayName, PreferredLanguage = "en" },
            userEmails: Array.Empty<UserEmail>(),
            eventParticipations: Array.Empty<EventParticipation>(),
            externalLogins: Array.Empty<(string, string)>(),
            profile: null,
            contactFields: Array.Empty<ContactField>(),
            profileLanguages: Array.Empty<ProfileLanguage>(),
            volunteerHistory: Array.Empty<VolunteerHistoryEntry>());

    [HumansFact]
    public async Task GetUserInfoAsync_DictMiss_DelegatesToInnerAndCaches()
    {
        var userId = Guid.NewGuid();
        var info = SampleUserInfo(userId);
        _inner.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(info));

        var sut = CreateSut();

        var first = await sut.GetUserInfoAsync(userId);
        first.Should().BeSameAs(info);
        await _inner.Received(1).GetUserInfoAsync(userId, Arg.Any<CancellationToken>());

        var second = await sut.GetUserInfoAsync(userId);
        second.Should().BeSameAs(info);
        // Second call should still be once — dict hit.
        await _inner.Received(1).GetUserInfoAsync(userId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetUserInfoAsync_InnerReturnsNull_DoesNotCacheAndKeepsAsking()
    {
        var userId = Guid.NewGuid();
        _inner.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>((UserInfo?)null));

        var sut = CreateSut();

        (await sut.GetUserInfoAsync(userId)).Should().BeNull();
        (await sut.GetUserInfoAsync(userId)).Should().BeNull();

        await _inner.Received(2).GetUserInfoAsync(userId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task UpdateDisplayNameAsync_RefreshesDictEntry()
    {
        var userId = Guid.NewGuid();

        // Prime cache with the stale entry.
        _inner.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(SampleUserInfo(userId, "Before")));

        // RefreshEntryAsync rebuild path: user repo + email repo + profile repo.
        var freshUser = SampleUser(userId);
        freshUser.DisplayName = "After";
        _userRepo.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(freshUser);
        _userEmailRepo.GetByUserIdReadOnlyAsync(userId, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<UserEmail>());
        _userRepo.GetEventParticipationsByUserIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<EventParticipation>());
        _userRepo.GetExternalLoginsByUserIdsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, IReadOnlyList<(string Provider, string ProviderKey)>>());
        _profileRepo.GetByUserIdReadOnlyAsync(userId, Arg.Any<CancellationToken>())
            .Returns((Profile?)null);

        var sut = CreateSut();
        await sut.GetUserInfoAsync(userId); // prime

        await sut.UpdateDisplayNameAsync(userId, "After");

        var fresh = await sut.GetUserInfoAsync(userId);
        fresh.Should().NotBeNull();
        fresh!.DisplayName.Should().Be("After");

        // Inner GetUserInfoAsync called only on the initial prime.
        await _inner.Received(1).GetUserInfoAsync(userId, Arg.Any<CancellationToken>());
        await _inner.Received(1).UpdateDisplayNameAsync(userId, "After", Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task InvalidateAsync_UserDeleted_RemovesEntry()
    {
        var userId = Guid.NewGuid();
        _inner.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(SampleUserInfo(userId)));
        _userRepo.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns((User?)null);

        var sut = CreateSut();
        await sut.GetUserInfoAsync(userId); // prime

        await ((IUserInfoInvalidator)sut).InvalidateAsync(userId);

        // Next read should miss the dict and delegate again.
        _inner.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>((UserInfo?)null));
        (await sut.GetUserInfoAsync(userId)).Should().BeNull();

        await _inner.Received(2).GetUserInfoAsync(userId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task InvalidateAsync_ExistingUser_ReloadsEntry()
    {
        var userId = Guid.NewGuid();

        // Prime with a stale entry first.
        _inner.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(SampleUserInfo(userId, "Before")));

        var freshUser = SampleUser(userId);
        freshUser.DisplayName = "After";
        _userRepo.GetByIdAsync(userId, Arg.Any<CancellationToken>()).Returns(freshUser);
        _userEmailRepo.GetByUserIdReadOnlyAsync(userId, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<UserEmail>());
        _userRepo.GetEventParticipationsByUserIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns(Array.Empty<EventParticipation>());
        _userRepo.GetExternalLoginsByUserIdsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, IReadOnlyList<(string Provider, string ProviderKey)>>());
        _profileRepo.GetByUserIdReadOnlyAsync(userId, Arg.Any<CancellationToken>())
            .Returns((Profile?)null);

        var sut = CreateSut();
        await sut.GetUserInfoAsync(userId);

        await ((IUserInfoInvalidator)sut).InvalidateAsync(userId);

        var fresh = await sut.GetUserInfoAsync(userId);
        fresh!.DisplayName.Should().Be("After");
        await _inner.Received(1).GetUserInfoAsync(userId, Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetUserInfoAsync_ConcurrentReads_ReturnSameEntryWithoutTearing()
    {
        var userId = Guid.NewGuid();
        var info = SampleUserInfo(userId);
        _inner.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(info));

        var sut = CreateSut();

        // Prime once so subsequent reads hit the dict.
        await sut.GetUserInfoAsync(userId);

        var tasks = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(async () => await sut.GetUserInfoAsync(userId)))
            .ToArray();
        var results = await Task.WhenAll(tasks);

        results.Should().AllSatisfy(r =>
        {
            r.Should().NotBeNull();
            r!.Id.Should().Be(userId);
        });
    }

    [HumansFact]
    public async Task WarmAllAsync_PopulatesDictForAllUsers_AndServesFromDict()
    {
        var userA = SampleUser();
        var userB = SampleUser();
        _userRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<User> { userA, userB });
        _userEmailRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<UserEmail>());
        _userRepo.GetExternalLoginsByUserIdsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, IReadOnlyList<(string Provider, string ProviderKey)>>());
        _userRepo.GetEventParticipationsByUserIdAsync(
                Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<EventParticipation>());
        _profileRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<Profile>());
        _contactFieldRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ContactField>());

        var sut = CreateSut();
        await sut.WarmAllAsync();

        var hitA = await sut.GetUserInfoAsync(userA.Id);
        var hitB = await sut.GetUserInfoAsync(userB.Id);

        hitA.Should().NotBeNull();
        hitB.Should().NotBeNull();
        await _inner.DidNotReceive().GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetUserInfoAsync_AllEightTablesFlowIntoUserInfo()
    {
        // Drives the inner UserService through a custom stub that returns
        // a UserInfo populated from every contributing table, then asserts
        // the cached payload exposes each piece.
        var userId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var emailId = Guid.NewGuid();
        var participationId = Guid.NewGuid();
        var contactFieldId = Guid.NewGuid();
        var languageId = Guid.NewGuid();
        var historyId = Guid.NewGuid();

        var user = new User
        {
            Id = userId, DisplayName = "Eight", PreferredLanguage = "es",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            GoogleEmailStatus = GoogleEmailStatus.Valid,
            ICalToken = Guid.NewGuid(),
        };
        var userEmail = new UserEmail
        {
            Id = emailId, UserId = userId, Email = "eight@example.com",
            IsVerified = true, IsPrimary = true, IsGoogle = true,
            Provider = "Google", ProviderKey = "subject-xyz",
        };
        var participation = new EventParticipation
        {
            Id = participationId, UserId = userId, Year = 2026,
            Status = ParticipationStatus.Ticketed,
            Source = ParticipationSource.TicketSync,
        };
        var profile = new Profile
        {
            Id = profileId, UserId = userId,
            BurnerName = "Octa", FirstName = "Eight", LastName = "Tables",
            DateOfBirth = new LocalDate(1990, 7, 4),
            IsApproved = true, MembershipTier = MembershipTier.Asociado,
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            UpdatedAt = Instant.FromUtc(2026, 1, 2, 0, 0),
        };
        profile.Languages.Add(new ProfileLanguage
        {
            Id = languageId, ProfileId = profileId,
            LanguageCode = "es", Proficiency = LanguageProficiency.Native,
        });
        profile.VolunteerHistory.Add(new VolunteerHistoryEntry
        {
            Id = historyId, ProfileId = profileId,
            Date = new LocalDate(2025, 3, 1),
            EventName = "Nowhere 2025",
        });
        var contactField = new ContactField
        {
            Id = contactFieldId, ProfileId = profileId,
            FieldType = ContactFieldType.Phone, Value = "+34 555 0001",
            Visibility = ContactFieldVisibility.AllActiveProfiles, DisplayOrder = 0,
        };
        var externalLogins = (IReadOnlyList<(string Provider, string ProviderKey)>)
            new List<(string Provider, string ProviderKey)>
            {
                ("Google", "ext-key-1"),
            };

        var fullInfo = UserInfo.Create(
            user,
            new[] { userEmail },
            new[] { participation },
            externalLogins,
            profile,
            new[] { contactField },
            profile.Languages.ToList(),
            profile.VolunteerHistory.ToList());

        _inner.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(fullInfo));

        var sut = CreateSut();
        var result = await sut.GetUserInfoAsync(userId);

        result.Should().NotBeNull();
        result!.Id.Should().Be(userId);
        result.DisplayName.Should().Be("Eight");
        result.PreferredLanguage.Should().Be("es");
        result.GoogleEmailStatus.Should().Be(GoogleEmailStatus.Valid);
        result.UserEmails.Should().ContainSingle(e =>
            e.Id == emailId && e.IsPrimary && e.IsGoogle && e.IsVerified);
        result.EventParticipations.Should().ContainSingle(p =>
            p.Id == participationId && p.Year == 2026 && p.Status == ParticipationStatus.Ticketed);
        result.ExternalLogins.Should().ContainSingle(l =>
            l.Provider == "Google" && l.ProviderKey == "ext-key-1");
        result.Profile.Should().NotBeNull();
        result.Profile!.Id.Should().Be(profileId);
        result.Profile.BurnerName.Should().Be("Octa");
        result.Profile.FirstName.Should().Be("Eight");
        result.Profile.LastName.Should().Be("Tables");
        result.Profile.BirthdayDay.Should().Be(4);
        result.Profile.BirthdayMonth.Should().Be(7);
        result.Profile.ContactFields.Should().ContainSingle(c =>
            c.Id == contactFieldId && c.FieldType == ContactFieldType.Phone);
        result.Profile.Languages.Should().ContainSingle(l =>
            l.Id == languageId && l.LanguageCode == "es");
        result.Profile.VolunteerHistory.Should().ContainSingle(v =>
            v.Id == historyId && v.EventName == "Nowhere 2025");
    }

    [HumansFact]
    public void UserInfo_DoesNotCarryProfilePictureData_OrYearOfBirth()
    {
        // Pin the design choices from issue #703:
        //  - ProfilePictureData (the large blob on Profile) is intentionally NOT
        //    projected into UserInfo / ProfileInfo.
        //  - The full DateOfBirth is NOT carried; only BirthdayDay + BirthdayMonth.
        var profileInfoType = typeof(ProfileInfo);
        var props = profileInfoType.GetProperties().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);

        props.Should().NotContain("ProfilePictureData");
        props.Should().NotContain("DateOfBirth");
        props.Should().NotContain("Year");
        props.Should().Contain("BirthdayDay");
        props.Should().Contain("BirthdayMonth");
    }

    [HumansFact]
    public async Task DeleteUsersAsync_EvictsAffectedEntries()
    {
        var u1 = Guid.NewGuid();
        var u2 = Guid.NewGuid();
        _inner.GetUserInfoAsync(u1, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(SampleUserInfo(u1)));
        _inner.GetUserInfoAsync(u2, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(SampleUserInfo(u2)));
        _inner.DeleteUsersAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var sut = CreateSut();
        await sut.GetUserInfoAsync(u1);
        await sut.GetUserInfoAsync(u2);

        var deleted = await sut.DeleteUsersAsync(new[] { u1, u2 });

        deleted.Should().Be(2);

        // Both reads should now refetch (entries were evicted).
        _inner.GetUserInfoAsync(u1, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>((UserInfo?)null));
        _inner.GetUserInfoAsync(u2, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>((UserInfo?)null));
        (await sut.GetUserInfoAsync(u1)).Should().BeNull();
        (await sut.GetUserInfoAsync(u2)).Should().BeNull();
    }
}
