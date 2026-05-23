using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Interfaces.Users;
using Humans.Application.Services.Profiles;
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
    private readonly ICommunicationPreferenceRepository _communicationPreferenceRepo = Substitute.For<ICommunicationPreferenceRepository>();

    private CachingUserService CreateSut()
    {
        var services = new ServiceCollection();
        services.AddKeyedScoped<IUserService>(CachingUserService.InnerServiceKey, (_, _) => _inner);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new CachingUserService(
            _userRepo, _userEmailRepo, _profileRepo, _contactFieldRepo,
            _communicationPreferenceRepo,
            scopeFactory, NullLogger<CachingUserService>.Instance);
    }

    private static User SampleUser(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        DisplayName = "Alice",
        PreferredLanguage = "en",
        CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
    };

    private static UserInfo SampleUserInfo(
        Guid userId,
        string displayName = "Alice",
        IReadOnlyList<EventParticipation>? eventParticipations = null) =>
        UserInfo.Create(
            new User { Id = userId, DisplayName = displayName, PreferredLanguage = "en" },
            userEmails: [],
            eventParticipations: eventParticipations ?? [],
            externalLogins: [],
            profile: null,
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: [],
            communicationPreferences: []);

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
            .Returns([]);
        _userRepo.GetEventParticipationsByUserIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns([]);
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
        fresh.BurnerName.Should().Be("After");

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

        var sut = CreateSut();
        await sut.GetUserInfoAsync(userId); // prime

        // User has been deleted in the source; inner now returns null.
        _inner.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>((UserInfo?)null));

        await sut.InvalidateAsync(userId);

        // Entry should be tombstoned by ReplaceAsync's null-load path.
        (await sut.GetUserInfoAsync(userId)).Should().BeNull();
    }

    [HumansFact]
    public async Task InvalidateAsync_ExistingUser_ReloadsEntry()
    {
        var userId = Guid.NewGuid();
        var stale = SampleUserInfo(userId, "Before");
        var fresh = SampleUserInfo(userId, "After");

        // Prime with the stale entry.
        _inner.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(stale));

        var sut = CreateSut();
        await sut.GetUserInfoAsync(userId);

        // Source row updated: inner now returns the fresh UserInfo.
        _inner.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(fresh));

        await sut.InvalidateAsync(userId);

        var hit = await sut.GetUserInfoAsync(userId);
        hit!.BurnerName.Should().Be("After");
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
            r.Id.Should().Be(userId);
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
            .Returns([]);
        _userRepo.GetExternalLoginsByUserIdsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, IReadOnlyList<(string Provider, string ProviderKey)>>());
        _userRepo.GetEventParticipationsByUserIdsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, IReadOnlyList<EventParticipation>>());
        _profileRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns([]);
        _contactFieldRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns([]);

        var sut = CreateSut();
        await ((IHostedService)sut).StartAsync(CancellationToken.None);

        await _userRepo.Received(1).GetEventParticipationsByUserIdsAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
        await _userRepo.DidNotReceive().GetEventParticipationsByUserIdAsync(
            Arg.Any<Guid>(), Arg.Any<CancellationToken>());

        var hitA = await sut.GetUserInfoAsync(userA.Id);
        var hitB = await sut.GetUserInfoAsync(userB.Id);

        hitA.Should().NotBeNull();
        hitB.Should().NotBeNull();
        await _inner.DidNotReceive().GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task LoadAllReads_TriggerWarmupOnDemand_WhenCold()
    {
        var userA = SampleUser();
        var userB = SampleUser();
        _userRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<User> { userA, userB });
        _userEmailRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns([]);
        _userRepo.GetExternalLoginsByUserIdsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, IReadOnlyList<(string Provider, string ProviderKey)>>());
        _userRepo.GetEventParticipationsByUserIdsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, IReadOnlyList<EventParticipation>>());
        _profileRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns([]);
        _contactFieldRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns([]);

        var sut = CreateSut();

        // No StartAsync — cache is cold. The first load-all read drives warmup.
        var all = await sut.GetAllUserInfosAsync();
        all.Should().HaveCount(2);

        // Subsequent load-all reads do not re-drive warmup.
        await _userRepo.Received(1).GetAllAsync(Arg.Any<CancellationToken>());
        var again = await sut.GetAllUserInfosAsync();
        again.Should().HaveCount(2);
        await _userRepo.Received(1).GetAllAsync(Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetUserInfosAsync_ColdCache_WarmsBeforeFallback_DoesNotLoadPerKey()
    {
        // Issue #743 regression. On cold cache, GetUserInfosAsync(ids) must
        // trigger the bulk WarmAllAsync exactly once instead of issuing a
        // per-id LoadRowAsync for each requested userId (which would emit
        // 7 SELECTs per user via the inner service on each miss).
        var userA = SampleUser();
        var userB = SampleUser();
        var userC = SampleUser();
        _userRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<User> { userA, userB, userC });
        _userEmailRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns([]);
        _userRepo.GetExternalLoginsByUserIdsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, IReadOnlyList<(string Provider, string ProviderKey)>>());
        _userRepo.GetEventParticipationsByUserIdsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, IReadOnlyList<EventParticipation>>());
        _profileRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns([]);
        _contactFieldRepo.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns([]);

        var sut = CreateSut();

        // Cold cache — no StartAsync. First call must warm in bulk, not per-id.
        var result = await sut.GetUserInfosAsync([userA.Id, userB.Id, userC.Id]);

        result.Should().HaveCount(3);

        // Bulk warm path ran exactly once.
        await _userRepo.Received(1).GetAllAsync(Arg.Any<CancellationToken>());
        // The inner per-id loader was never called — the bug would have
        // routed each of the three misses through inner.GetUserInfoAsync.
        await _inner.DidNotReceive().GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task SingleKeyReads_StillWorkBeforeWarmup()
    {
        var userId = Guid.NewGuid();
        var info = SampleUserInfo(userId);
        _inner.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(info));

        var sut = CreateSut();

        // Cache is cold — GetUserInfoAsync lazy-fills, no warmup gate.
        var first = await sut.GetUserInfoAsync(userId);
        first.Should().BeSameAs(info);
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
            Id = userId,
            DisplayName = "Eight",
            PreferredLanguage = "es",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            GoogleEmailStatus = GoogleEmailStatus.Valid,
            ICalToken = Guid.NewGuid(),
        };
        var userEmail = new UserEmail
        {
            Id = emailId,
            UserId = userId,
            Email = "eight@example.com",
            IsVerified = true,
            IsPrimary = true,
            IsGoogle = true,
            Provider = "Google",
            ProviderKey = "subject-xyz",
        };
        var participation = new EventParticipation
        {
            Id = participationId,
            UserId = userId,
            Year = 2026,
            Status = ParticipationStatus.Ticketed,
            Source = ParticipationSource.TicketSync,
        };
        var profile = new Profile
        {
            Id = profileId,
            UserId = userId,
            BurnerName = "Octa",
            FirstName = "Eight",
            LastName = "Tables",
            DateOfBirth = new LocalDate(1990, 7, 4),
            IsApproved = true,
            MembershipTier = MembershipTier.Asociado,
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            UpdatedAt = Instant.FromUtc(2026, 1, 2, 0, 0),
        };
        profile.Languages.Add(new ProfileLanguage
        {
            Id = languageId,
            ProfileId = profileId,
            LanguageCode = "es",
            Proficiency = LanguageProficiency.Native,
        });
        profile.VolunteerHistory.Add(new VolunteerHistoryEntry
        {
            Id = historyId,
            ProfileId = profileId,
            Date = new LocalDate(2025, 3, 1),
            EventName = "Nowhere 2025",
        });
        var contactField = new ContactField
        {
            Id = contactFieldId,
            ProfileId = profileId,
            FieldType = ContactFieldType.Phone,
            Value = "+34 555 0001",
            Visibility = ContactFieldVisibility.AllActiveProfiles,
            DisplayOrder = 0,
        };
        var externalLogins = (IReadOnlyList<(string Provider, string ProviderKey)>)
            new List<(string Provider, string ProviderKey)>
            {
                ("Google", "ext-key-1"),
            };

        var fullInfo = UserInfo.Create(
            user, [userEmail], [participation],
            externalLogins,
            profile, [contactField],
            profile.Languages.ToList(),
            profile.VolunteerHistory.ToList(),
            []);

        _inner.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(fullInfo));

        var sut = CreateSut();
        var result = await sut.GetUserInfoAsync(userId);

        result.Should().NotBeNull();
        result.Id.Should().Be(userId);
        result.BurnerName.Should().Be("Octa");
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
    public async Task GetByIdsAsync_WarmCache_ServesFromDict_ZeroInnerCalls()
    {
        // Issue #744. Warm cache + GetByIdsAsync(known ids) must never call
        // the inner service (zero EF queries). The single GetByIdsAsync always
        // returns Users with UserEmails populated — there is no "without
        // emails" variant because there is nothing else the cache can serve.
        var userId = Guid.NewGuid();
        var emailId = Guid.NewGuid();
        var user = new User
        {
            Id = userId,
            DisplayName = "Cached",
            PreferredLanguage = "es",
            ProfilePictureUrl = "https://example.com/pic.png",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            GoogleEmailStatus = GoogleEmailStatus.Valid,
        };
        var userEmail = new UserEmail
        {
            Id = emailId,
            UserId = userId,
            Email = "cached@example.com",
            IsVerified = true,
            IsPrimary = true,
            IsGoogle = true,
            Provider = "Google",
            ProviderKey = "subj-1",
        };
        var info = UserInfo.Create(
            user, [userEmail],
            eventParticipations: [],
            externalLogins: [],
            profile: null,
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: [],
            communicationPreferences: []);

        _inner.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(info));

        var sut = CreateSut();

        // Prime the cache.
        await sut.GetUserInfoAsync(userId);

        // Warm-cache call should rehydrate locally, never delegate.
        var result = await sut.GetByIdsAsync([userId]);

        result.Should().ContainKey(userId);
        var hit = result[userId];
#pragma warning disable CS0618 // DisplayName legacy mirror — preservation is the point of this test.
        hit.DisplayName.Should().Be("Cached");
#pragma warning restore CS0618
        hit.ProfilePictureUrl.Should().Be("https://example.com/pic.png");
        hit.GoogleEmailStatus.Should().Be(GoogleEmailStatus.Valid);
        hit.UserEmails.Should().ContainSingle(e =>
            e.Id == emailId && e.Email == "cached@example.com" && e.IsPrimary && e.IsGoogle && e.IsVerified);

        await _inner.DidNotReceive().GetByIdsAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetByIdsAsync_PartialHit_OnlyMissesDelegated()
    {
        // Issue #744. Warm hits served from dict, misses batched to inner.
        var hitId = Guid.NewGuid();
        var missId = Guid.NewGuid();

        var info = SampleUserInfo(hitId, "Cached");
        _inner.GetUserInfoAsync(hitId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(info));

        var missUser = SampleUser(missId);
#pragma warning disable CS0618
        missUser.DisplayName = "Fresh";
#pragma warning restore CS0618
        _inner.GetByIdsAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(missId)),
            Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, User> { [missId] = missUser });

        var sut = CreateSut();
        await sut.GetUserInfoAsync(hitId); // prime only the hit.

        var result = await sut.GetByIdsAsync([hitId, missId]);

        result.Should().HaveCount(2);
#pragma warning disable CS0618
        result[hitId].DisplayName.Should().Be("Cached");
        result[missId].DisplayName.Should().Be("Fresh");
#pragma warning restore CS0618

        // Inner was called for the miss with only the missing id.
        await _inner.Received(1).GetByIdsAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(missId)),
            Arg.Any<CancellationToken>());
    }

    [HumansFact]
    public async Task GetByIdAsync_WarmCache_ServesFromDict_NoInnerCall()
    {
        var userId = Guid.NewGuid();
        var info = SampleUserInfo(userId, "Cached");
        _inner.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(info));

        var sut = CreateSut();
        await sut.GetUserInfoAsync(userId); // prime

        var user = await sut.GetByIdAsync(userId);

        user.Should().NotBeNull();
#pragma warning disable CS0618
        user.DisplayName.Should().Be("Cached");
#pragma warning restore CS0618

        // GetByIdAsync should not have been delegated.
        await _inner.DidNotReceive().GetByIdAsync(userId, Arg.Any<CancellationToken>());
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

        var deleted = await sut.DeleteUsersAsync([u1, u2]);

        deleted.Should().Be(2);

        // Both reads should now refetch (entries were evicted).
        _inner.GetUserInfoAsync(u1, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>((UserInfo?)null));
        _inner.GetUserInfoAsync(u2, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>((UserInfo?)null));
        (await sut.GetUserInfoAsync(u1)).Should().BeNull();
        (await sut.GetUserInfoAsync(u2)).Should().BeNull();
    }

    [HumansFact]
    public async Task SetParticipationFromTicketSyncAsync_DelegatesAndRefreshesParticipationSlice()
    {
        // Decorator forwards unconditionally — idempotency lives in the inner
        // service / repository (Attended is terminal, identical TicketSync row
        // is a no-op upsert). The decorator's only job is to delegate, then
        // refresh the participation slice in the dict.
        var userId = Guid.NewGuid();
        var stale = new EventParticipation
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Year = 2026,
            Status = ParticipationStatus.NotAttending,
            Source = ParticipationSource.UserDeclared,
            DeclaredAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        };
        var fresh = new EventParticipation
        {
            Id = stale.Id,
            UserId = userId,
            Year = 2026,
            Status = ParticipationStatus.Ticketed,
            Source = ParticipationSource.TicketSync,
            DeclaredAt = null,
        };

        _inner.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(SampleUserInfo(userId, eventParticipations: [stale])));
        _userRepo.GetEventParticipationsByUserIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns([fresh]);

        var sut = CreateSut();
        await sut.GetUserInfoAsync(userId);

        await sut.SetParticipationFromTicketSyncAsync(userId, 2026, ParticipationStatus.Ticketed, checkedInAt: null);

        await _inner.Received(1).SetParticipationFromTicketSyncAsync(
            userId, 2026, ParticipationStatus.Ticketed, null, Arg.Any<CancellationToken>());
        await _userRepo.Received(1).GetEventParticipationsByUserIdAsync(userId, Arg.Any<CancellationToken>());
        // Slice refresh — sibling slices and repos are not touched.
        await _userEmailRepo.DidNotReceive().GetByUserIdReadOnlyAsync(userId, Arg.Any<CancellationToken>());
        await _profileRepo.DidNotReceive().GetByUserIdReadOnlyAsync(userId, Arg.Any<CancellationToken>());
        await _contactFieldRepo.DidNotReceive().GetByProfileIdReadOnlyAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _communicationPreferenceRepo.DidNotReceive().GetByUserIdReadOnlyAsync(userId, Arg.Any<CancellationToken>());

        var info = await sut.GetUserInfoAsync(userId);
        info!.EventParticipations.Should().ContainSingle(p =>
            p.Year == 2026 && p.Status == ParticipationStatus.Ticketed && p.Source == ParticipationSource.TicketSync);
    }

    [HumansFact]
    public async Task RemoveTicketSyncParticipationAsync_DelegatesAndRefreshesParticipationSlice()
    {
        var userId = Guid.NewGuid();
        var existing = new EventParticipation
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Year = 2026,
            Status = ParticipationStatus.Ticketed,
            Source = ParticipationSource.TicketSync,
            DeclaredAt = null,
        };

        _inner.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(SampleUserInfo(userId, eventParticipations: [existing])));
        _userRepo.GetEventParticipationsByUserIdAsync(userId, Arg.Any<CancellationToken>())
            .Returns([]);

        var sut = CreateSut();
        await sut.GetUserInfoAsync(userId);

        await sut.RemoveTicketSyncParticipationAsync(userId, 2026);

        await _inner.Received(1).RemoveTicketSyncParticipationAsync(userId, 2026, Arg.Any<CancellationToken>());
        await _userRepo.Received(1).GetEventParticipationsByUserIdAsync(userId, Arg.Any<CancellationToken>());

        var info = await sut.GetUserInfoAsync(userId);
        info!.EventParticipations.Should().BeEmpty();
    }

    // ==========================================================================
    // SearchUsersAsync — per-record matcher buckets, pre-filter rules,
    // admin GUID short-circuit. The matcher reads from the cached UserInfo dict;
    // tests prime the cache via GetUserInfoAsync then invoke SearchUsersAsync.
    // ==========================================================================

    private static UserInfo BuildSearchableUserInfo(
        Guid userId,
        string? burnerName = null,
        string? city = null,
        string? bio = null,
        string? pronouns = null,
        string? contributionInterests = null,
        bool isApproved = true,
        bool isSuspended = false,
        bool isRejected = false,
        IReadOnlyList<(string Email, bool IsVerified, bool IsPrimary)>? emails = null,
        IReadOnlyList<(string EventName, string? Description)>? volunteerHistory = null,
        IReadOnlyList<(ContactFieldType Type, string Value, ContactFieldVisibility Visibility)>? contactFields = null)
    {
        var user = new User
        {
            Id = userId,
            DisplayName = burnerName ?? "Display",
            PreferredLanguage = "en",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        };

        var userEmails = (emails ?? [])
            .Select(e => new UserEmail
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                Email = e.Email,
                IsVerified = e.IsVerified,
                IsPrimary = e.IsPrimary,
            })
            .ToList();

        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BurnerName = burnerName ?? string.Empty,
            FirstName = "First",
            LastName = "Last",
            City = city,
            Bio = bio,
            Pronouns = pronouns,
            ContributionInterests = contributionInterests,
            IsApproved = isApproved,
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            State = isSuspended ? ProfileState.Suspended : (isApproved ? ProfileState.Active : ProfileState.Stub),
            RejectedAt = isRejected ? Instant.FromUtc(2026, 1, 1, 0, 0) : null,
        };

        var cfRows = (contactFields ?? [])
            .Select((cf, i) => new ContactField
            {
                Id = Guid.NewGuid(),
                ProfileId = profile.Id,
                FieldType = cf.Type,
                Value = cf.Value,
                Visibility = cf.Visibility,
                DisplayOrder = i,
            })
            .ToList();

        var vhRows = (volunteerHistory ?? [])
            .Select(v => new VolunteerHistoryEntry
            {
                Id = Guid.NewGuid(),
                ProfileId = profile.Id,
                Date = new LocalDate(2025, 1, 1),
                EventName = v.EventName,
                Description = v.Description,
            })
            .ToList();

        return UserInfo.Create(
            user, userEmails,
            eventParticipations: [],
            externalLogins: [],
            profile, cfRows,
            profileLanguages: [],
            volunteerHistory: vhRows,
            communicationPreferences: []);
    }

    private async Task PrimeAsync(CachingUserService sut, UserInfo info)
    {
        _inner.GetUserInfoAsync(info.Id, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(info));
        await sut.GetUserInfoAsync(info.Id);
        // SearchUsersAsync / GetAllUserInfos / GetAllParticipationsForYearAsync /
        // GetMergedSourceIdsAsync gate on IsWarmedUp — these search tests seed
        // a single entry directly via GetUserInfoAsync instead of driving a
        // full WarmAllAsync, so flip the flag manually here.
        sut.MarkWarmedForTesting();
    }

    [HumansFact]
    public async Task SearchUsersAsync_PublicAll_MatchesByBurnerName()
    {
        var userId = Guid.NewGuid();
        var sut = CreateSut();
        await PrimeAsync(sut, BuildSearchableUserInfo(userId, burnerName: "Burner Bob"));

        var results = await sut.SearchUsersAsync("Burner", PersonSearchFields.PublicAll);

        results.Should().HaveCount(1);
        results[0].UserId.Should().Be(userId);
        results[0].MatchField.Should().Be("Name");
    }

    [HumansFact]
    public async Task SearchUsersAsync_PublicAll_MatchesByCity()
    {
        var sut = CreateSut();
        await PrimeAsync(sut, BuildSearchableUserInfo(Guid.NewGuid(), city: "Barcelona"));

        var results = await sut.SearchUsersAsync("Barcelona", PersonSearchFields.PublicAll);

        results.Should().HaveCount(1);
        results[0].MatchField.Should().Be("City");
    }

    [HumansFact]
    public async Task SearchUsersAsync_PublicAll_MatchesByBio()
    {
        var sut = CreateSut();
        await PrimeAsync(sut, BuildSearchableUserInfo(Guid.NewGuid(), bio: "I love fire dancing and community building"));

        var results = await sut.SearchUsersAsync("fire dancing", PersonSearchFields.PublicAll);

        results.Should().HaveCount(1);
        results[0].MatchField.Should().Be("Bio");
    }

    [HumansFact]
    public async Task SearchUsersAsync_PublicAll_IncludesSuspended()
    {
        var sut = CreateSut();
        await PrimeAsync(sut, BuildSearchableUserInfo(Guid.NewGuid(), city: "Madrid", isSuspended: true));

        var results = await sut.SearchUsersAsync("Madrid", PersonSearchFields.PublicAll);

        results.Should().HaveCount(1);
    }

    [HumansFact]
    public async Task SearchUsersAsync_PublicAll_IncludesUnapproved()
    {
        var sut = CreateSut();
        await PrimeAsync(sut, BuildSearchableUserInfo(Guid.NewGuid(), city: "Madrid", isApproved: false));

        var results = await sut.SearchUsersAsync("Madrid", PersonSearchFields.PublicAll);

        results.Should().HaveCount(1);
    }

    [HumansFact]
    public async Task SearchUsersAsync_PublicAll_ExcludesRejected()
    {
        var sut = CreateSut();
        await PrimeAsync(sut, BuildSearchableUserInfo(Guid.NewGuid(), city: "Madrid", isRejected: true));

        var results = await sut.SearchUsersAsync("Madrid", PersonSearchFields.PublicAll);

        results.Should().BeEmpty();
    }

    [HumansFact]
    public async Task SearchUsersAsync_None_ReturnsEmpty()
    {
        var sut = CreateSut();
        await PrimeAsync(sut, BuildSearchableUserInfo(Guid.NewGuid(), burnerName: "Match"));

        var results = await sut.SearchUsersAsync("Match", PersonSearchFields.None);

        results.Should().BeEmpty();
    }

    [HumansFact]
    public async Task SearchUsersAsync_NoMatch_ReturnsEmpty()
    {
        var sut = CreateSut();
        await PrimeAsync(sut, BuildSearchableUserInfo(Guid.NewGuid(), burnerName: "Alice"));

        var results = await sut.SearchUsersAsync("zzzznonexistent", PersonSearchFields.PublicAll);

        results.Should().BeEmpty();
    }

    [HumansFact]
    public async Task SearchUsersAsync_AdminBit_IncludesSuspended()
    {
        var sut = CreateSut();
        await PrimeAsync(sut, BuildSearchableUserInfo(Guid.NewGuid(), city: "Madrid", isSuspended: true));

        var results = await sut.SearchUsersAsync("Madrid", PersonSearchFields.AdminAll);

        results.Should().HaveCount(1);
    }

    [HumansFact]
    public async Task SearchUsersAsync_AdminBit_ExactUserIdLookup()
    {
        var userId = Guid.NewGuid();
        var sut = CreateSut();
        await PrimeAsync(sut, BuildSearchableUserInfo(userId, burnerName: "Alice"));

        var results = await sut.SearchUsersAsync(userId.ToString(), PersonSearchFields.AdminAll);

        results.Should().HaveCount(1);
        results[0].UserId.Should().Be(userId);
        results[0].MatchField.Should().Be("User ID");
    }

    [HumansFact]
    public async Task SearchUsersAsync_PublicAll_GuidShortCircuitsById()
    {
        var userId = Guid.NewGuid();
        var sut = CreateSut();
        await PrimeAsync(sut, BuildSearchableUserInfo(userId, burnerName: "Alice"));

        var results = await sut.SearchUsersAsync(userId.ToString(), PersonSearchFields.PublicAll);

        results.Should().HaveCount(1);
        results[0].UserId.Should().Be(userId);
        results[0].MatchField.Should().Be("User ID");
    }

    [HumansFact]
    public async Task SearchUsersAsync_AdminBit_GuidNotFound_ReturnsEmpty()
    {
        var sut = CreateSut();
        await PrimeAsync(sut, BuildSearchableUserInfo(Guid.NewGuid(), burnerName: "Alice"));

        var results = await sut.SearchUsersAsync(Guid.NewGuid().ToString(), PersonSearchFields.AdminAll);

        results.Should().BeEmpty();
    }

    [HumansFact]
    public async Task SearchUsersAsync_AdminBit_MatchesVerifiedEmail()
    {
        var sut = CreateSut();
        await PrimeAsync(sut, BuildSearchableUserInfo(
            Guid.NewGuid(),
            burnerName: "Alice",
            emails: [("alice@example.com", true, true)]));

        var results = await sut.SearchUsersAsync("alice@example.com", PersonSearchFields.AdminAll);

        results.Should().HaveCount(1);
        results[0].MatchField.Should().Be("Email");
        results[0].MatchedEmail.Should().Be("alice@example.com");
    }
}
