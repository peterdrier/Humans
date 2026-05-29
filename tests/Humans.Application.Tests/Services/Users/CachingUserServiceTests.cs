using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;
using Humans.Application.Interfaces.Onboarding;
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
    private static readonly System.Reflection.PropertyInfo LegacyDisplayNameProperty =
        typeof(User).GetProperty("DisplayName")
        ?? throw new InvalidOperationException("User.DisplayName property missing.");

    private readonly IUserService _inner = Substitute.For<IUserService>();

    private CachingUserService CreateSut()
    {
        var services = new ServiceCollection();
        services.AddKeyedScoped<IUserService>(CachingUserService.InnerServiceKey, (_, _) => _inner);
        var scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return new CachingUserService(
            scopeFactory, NullLogger<CachingUserService>.Instance);
    }

    private static User SampleUser(Guid? id = null) => new()
    {
        Id = id ?? Guid.NewGuid(),
        PreferredLanguage = "en",
        CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
    };

    private static UserInfo SampleUserInfo(
        Guid userId,
        string displayName = "Alice",
        IReadOnlyList<EventParticipation>? eventParticipations = null) =>
        UserInfo.Create(
            new User { Id = userId, PreferredLanguage = "en" },
            userEmails: [],
            eventParticipations: eventParticipations ?? [],
            externalLogins: [],
            profile: SampleProfile(userId, displayName),
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: [],
            communicationPreferences: []);

    private static Profile SampleProfile(Guid userId, string burnerName) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        BurnerName = burnerName,
        CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        UpdatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
        State = ProfileState.Active,
        IsApproved = true,
    };

    private static User WithLegacyDisplayName(User user, string displayName)
    {
        LegacyDisplayNameProperty.SetValue(user, displayName);
        return user;
    }

    private static string LegacyDisplayName(User user) =>
        (string?)LegacyDisplayNameProperty.GetValue(user) ?? string.Empty;

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
        _inner.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns([SampleUserInfo(userA.Id), SampleUserInfo(userB.Id)]);

        var sut = CreateSut();
        await ((IHostedService)sut).StartAsync(CancellationToken.None);

        await _inner.Received(1).GetAllUserInfosAsync(Arg.Any<CancellationToken>());

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
        _inner.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns([SampleUserInfo(userA.Id), SampleUserInfo(userB.Id)]);

        var sut = CreateSut();

        // No StartAsync — cache is cold. The first load-all read drives warmup.
        var all = await sut.GetAllUserInfosAsync();
        all.Should().HaveCount(2);

        // Subsequent load-all reads do not re-drive warmup.
        await _inner.Received(1).GetAllUserInfosAsync(Arg.Any<CancellationToken>());
        var again = await sut.GetAllUserInfosAsync();
        again.Should().HaveCount(2);
        await _inner.Received(1).GetAllUserInfosAsync(Arg.Any<CancellationToken>());
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
        _inner.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns([SampleUserInfo(userA.Id), SampleUserInfo(userB.Id), SampleUserInfo(userC.Id)]);

        var sut = CreateSut();

        // Cold cache — no StartAsync. First call must warm in bulk, not per-id.
        var result = await sut.GetUserInfosAsync([userA.Id, userB.Id, userC.Id]);

        result.Should().HaveCount(3);

        // Bulk warm path ran exactly once.
        await _inner.Received(1).GetAllUserInfosAsync(Arg.Any<CancellationToken>());
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
            profile: SampleProfile(userId, "Cached"),
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
        LegacyDisplayName(hit).Should().Be("Cached");
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

        var missUser = WithLegacyDisplayName(SampleUser(missId), "Fresh");
        _inner.GetByIdsAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(missId)),
            Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, User> { [missId] = missUser });

        var sut = CreateSut();
        await sut.GetUserInfoAsync(hitId); // prime only the hit.

        var result = await sut.GetByIdsAsync([hitId, missId]);

        result.Should().HaveCount(2);
        LegacyDisplayName(result[hitId]).Should().Be("Cached");
        LegacyDisplayName(result[missId]).Should().Be("Fresh");

        // Inner was called for the miss with only the missing id.
        await _inner.Received(1).GetByIdsAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1 && ids.Contains(missId)),
            Arg.Any<CancellationToken>());
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
            .Returns(
                new ValueTask<UserInfo?>(SampleUserInfo(userId, eventParticipations: [stale])),
                new ValueTask<UserInfo?>(SampleUserInfo(userId, eventParticipations: [fresh])));

        var sut = CreateSut();
        await sut.GetUserInfoAsync(userId);

        await sut.SetParticipationFromTicketSyncAsync(userId, 2026, ParticipationStatus.Ticketed, checkedInAt: null);

        await _inner.Received(1).SetParticipationFromTicketSyncAsync(
            userId, 2026, ParticipationStatus.Ticketed, null, Arg.Any<CancellationToken>());
        await _inner.Received(2).GetUserInfoAsync(userId, Arg.Any<CancellationToken>());
        // Slice refresh — sibling slices and repos are not touched.
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
            .Returns(
                new ValueTask<UserInfo?>(SampleUserInfo(userId, eventParticipations: [existing])),
                new ValueTask<UserInfo?>(SampleUserInfo(userId, eventParticipations: [])));

        var sut = CreateSut();
        await sut.GetUserInfoAsync(userId);

        await sut.RemoveTicketSyncParticipationAsync(userId, 2026);

        await _inner.Received(1).RemoveTicketSyncParticipationAsync(userId, 2026, Arg.Any<CancellationToken>());
        await _inner.Received(2).GetUserInfoAsync(userId, Arg.Any<CancellationToken>());

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

    // --- Profile-storage consolidation: decorator delegates to inner and
    // refreshes the cached UserInfo slice. RefreshEntryAsync reloads via
    // _inner.GetUserInfoAsync, so StubRefreshEntry overrides that return.

    private static UserInfo UserInfoFor(Guid userId, Profile? profile) =>
        UserInfo.Create(
            new User { Id = userId, PreferredLanguage = "en" },
            userEmails: [],
            eventParticipations: [],
            externalLogins: [],
            profile: profile,
            contactFields: [],
            profileLanguages: profile?.Languages.ToList() ?? [],
            volunteerHistory: profile?.VolunteerHistory.ToList() ?? [],
            communicationPreferences: []);

    private static UserInfo UserInfoWithEmail(Guid userId, string email) =>
        UserInfo.Create(
            new User { Id = userId, PreferredLanguage = "en" },
            userEmails:
            [
                new UserEmail
                {
                    Id = Guid.NewGuid(),
                    UserId = userId,
                    Email = email,
                    IsVerified = true,
                    IsPrimary = true,
                    CreatedAt = Instant.MinValue,
                    UpdatedAt = Instant.MinValue,
                }
            ],
            eventParticipations: [],
            externalLogins: [],
            profile: SampleProfile(userId, "Alice"),
            contactFields: [],
            profileLanguages: [],
            volunteerHistory: [],
            communicationPreferences: []);

    private void StubRefreshEntry(Guid userId, Profile? profile) =>
        _inner.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfoFor(userId, profile)));

    [HumansFact]
    public async Task SaveProfileAsync_RefreshesProfileAndDisplayNameSlice()
    {
        var userId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var sut = CreateSut();
        await PrimeAsync(sut, SampleUserInfo(userId, "Before"));

        _inner.SaveProfileAsync(
                userId,
                Arg.Any<UserProfileSaveCommand>(),
                Arg.Any<CancellationToken>())
            .Returns(new UserProfileSaveResult(profileId, null, "image/png"));

        StubRefreshEntry(userId, new Profile
        {
            Id = profileId,
            UserId = userId,
            BurnerName = "New Burner",
            FirstName = "New",
            LastName = "Human",
            ProfilePictureContentType = "image/png",
            State = ProfileState.Active,
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            UpdatedAt = Instant.FromUtc(2026, 1, 2, 0, 0),
        });

        var result = await sut.SaveProfileAsync(
            userId,
            new UserProfileSaveCommand(
                DisplayName: "After",
                BurnerName: "New Burner",
                FirstName: "New",
                LastName: "Human",
                City: null,
                CountryCode: null,
                Latitude: null,
                Longitude: null,
                PlaceId: null,
                Bio: null,
                Pronouns: null,
                ContributionInterests: null,
                BoardNotes: null,
                BirthdayMonth: null,
                BirthdayDay: null,
                EmergencyContactName: null,
                EmergencyContactPhone: null,
                EmergencyContactRelationship: null,
                NoPriorBurnExperience: false,
                PictureMutation: UserProfilePictureMutation.Set,
                ProfilePictureContentType: "image/png"));

        result.ProfileId.Should().Be(profileId);
        await _inner.Received(1).SaveProfileAsync(
            userId, Arg.Any<UserProfileSaveCommand>(), Arg.Any<CancellationToken>());
        var refreshed = await sut.GetUserInfoAsync(userId);
        refreshed.Should().NotBeNull();
        refreshed!.BurnerName.Should().Be("New Burner");
        refreshed.Profile.Should().NotBeNull();
        refreshed.Profile!.BurnerName.Should().Be("New Burner");
        refreshed.Profile.ProfilePictureContentType.Should().Be("image/png");
    }

    [HumansFact]
    public async Task SetProfilePictureContentTypeAsync_RefreshesProfilePictureSlice()
    {
        var userId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var sut = CreateSut();
        await PrimeAsync(sut, SampleUserInfo(userId));

        _inner.SetProfilePictureContentTypeAsync(userId, "image/webp", Arg.Any<CancellationToken>())
            .Returns(new UserProfilePictureContentTypeResult(true, profileId, "image/png", "image/webp"));

        StubRefreshEntry(userId, new Profile
        {
            Id = profileId,
            UserId = userId,
            BurnerName = "Alice",
            ProfilePictureContentType = "image/webp",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            UpdatedAt = Instant.FromUtc(2026, 1, 2, 0, 0),
        });

        var result = await sut.SetProfilePictureContentTypeAsync(userId, "image/webp");

        result.Saved.Should().BeTrue();
        result.PreviousProfilePictureContentType.Should().Be("image/png");
        var refreshed = await sut.GetUserInfoAsync(userId);
        refreshed!.Profile.Should().NotBeNull();
        refreshed.Profile!.ProfilePictureContentType.Should().Be("image/webp");
    }

    [HumansFact]
    public async Task AnonymizeProfileForDeletionAsync_RefreshesAnonymizedProfileSlice()
    {
        var userId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var sut = CreateSut();
        await PrimeAsync(sut, SampleUserInfo(userId));

        _inner.AnonymizeProfileForDeletionAsync(userId, Arg.Any<CancellationToken>())
            .Returns(new UserProfileAnonymizeResult(true, profileId, "image/png"));

        StubRefreshEntry(userId, new Profile
        {
            Id = profileId,
            UserId = userId,
            BurnerName = "",
            FirstName = "Deleted",
            LastName = "User",
            ProfilePictureContentType = null,
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            UpdatedAt = Instant.FromUtc(2026, 1, 2, 0, 0),
        });

        var result = await sut.AnonymizeProfileForDeletionAsync(userId);

        result.Anonymized.Should().BeTrue();
        result.PreviousProfilePictureContentType.Should().Be("image/png");
        var refreshed = await sut.GetUserInfoAsync(userId);
        refreshed!.Profile.Should().NotBeNull();
        refreshed.Profile!.FirstName.Should().Be("Deleted");
        refreshed.Profile.ProfilePictureContentType.Should().BeNull();
    }

    [HumansFact]
    public async Task ApplyProfileOnboardingMutationAsync_RefreshesProfileSlice()
    {
        var userId = Guid.NewGuid();
        var sut = CreateSut();
        await PrimeAsync(sut, SampleUserInfo(userId));

        _inner.ApplyProfileOnboardingMutationAsync(
                userId,
                Arg.Any<UserProfileOnboardingCommand>(),
                Arg.Any<CancellationToken>())
            .Returns(new OnboardingResult(true));

        StubRefreshEntry(userId, new Profile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            BurnerName = "Alice",
            FirstName = "Alice",
            LastName = "Example",
            IsApproved = true,
            State = ProfileState.Active,
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            UpdatedAt = Instant.FromUtc(2026, 1, 2, 0, 0),
        });

        await sut.ApplyProfileOnboardingMutationAsync(
            userId,
            new UserProfileOnboardingCommand(UserProfileOnboardingMutation.ApproveVolunteer));

        var refreshed = await sut.GetUserInfoAsync(userId);
        refreshed!.Profile.Should().NotBeNull();
        refreshed.Profile!.IsApproved.Should().BeTrue();
        refreshed.Profile.State.Should().Be(ProfileState.Active);
    }

    [HumansFact]
    public async Task SaveProfileLanguagesAsync_RefreshesLanguageSliceForOwner()
    {
        var userId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var sut = CreateSut();
        await PrimeAsync(sut, SampleUserInfo(userId));

        _inner.SaveProfileLanguagesAsync(
                profileId,
                Arg.Any<IReadOnlyList<ProfileLanguage>>(),
                Arg.Any<CancellationToken>())
            .Returns(new UserProfileLanguagesSaveResult(true, userId));

        var profile = new Profile
        {
            Id = profileId,
            UserId = userId,
            BurnerName = "Alice",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            UpdatedAt = Instant.FromUtc(2026, 1, 2, 0, 0),
        };
        profile.Languages.Add(new ProfileLanguage
        {
            Id = Guid.NewGuid(),
            ProfileId = profileId,
            LanguageCode = "es",
            Proficiency = LanguageProficiency.Native,
        });
        StubRefreshEntry(userId, profile);

        var result = await sut.SaveProfileLanguagesAsync(
            profileId,
            [
                new ProfileLanguage
                {
                    ProfileId = profileId,
                    LanguageCode = "es",
                    Proficiency = LanguageProficiency.Native,
                },
            ]);

        result.Saved.Should().BeTrue();
        result.UserId.Should().Be(userId);
        var refreshed = await sut.GetUserInfoAsync(userId);
        refreshed!.Profile.Should().NotBeNull();
        refreshed.Profile!.Languages.Should().ContainSingle(l =>
            l.LanguageCode == "es" && l.Proficiency == LanguageProficiency.Native);
    }

    [HumansFact]
    public async Task SaveProfileVolunteerHistoryAsync_RefreshesVolunteerHistorySlice()
    {
        var userId = Guid.NewGuid();
        var profileId = Guid.NewGuid();
        var sut = CreateSut();
        await PrimeAsync(sut, SampleUserInfo(userId));

        _inner.SaveProfileVolunteerHistoryAsync(
                userId,
                Arg.Any<IReadOnlyList<CVEntry>>(),
                Arg.Any<CancellationToken>())
            .Returns(true);

        var profile = new Profile
        {
            Id = profileId,
            UserId = userId,
            BurnerName = "Alice",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            UpdatedAt = Instant.FromUtc(2026, 1, 2, 0, 0),
        };
        profile.VolunteerHistory.Add(new VolunteerHistoryEntry
        {
            Id = Guid.NewGuid(),
            ProfileId = profileId,
            Date = new LocalDate(2025, 3, 1),
            EventName = "Nowhere 2025",
            Description = "Sound crew",
        });
        StubRefreshEntry(userId, profile);

        var saved = await sut.SaveProfileVolunteerHistoryAsync(
            userId,
            [new CVEntry(Guid.Empty, new LocalDate(2025, 3, 1), "Nowhere 2025", "Sound crew")]);

        saved.Should().BeTrue();
        var refreshed = await sut.GetUserInfoAsync(userId);
        refreshed!.Profile.Should().NotBeNull();
        refreshed.Profile!.VolunteerHistory.Should().ContainSingle(v =>
            v.EventName == "Nowhere 2025" && v.Description == "Sound crew");
    }

    [HumansFact]
    public async Task ApplyUserEmailReconcilePlanAsync_RefreshesEveryMutatedUsersEmailSlice()
    {
        var signingUserId = Guid.NewGuid();
        var displacedUserId = Guid.NewGuid();
        var sut = CreateSut();

        await PrimeAsync(sut, SampleUserInfo(signingUserId));
        await PrimeAsync(sut, SampleUserInfo(displacedUserId));

        _inner.ApplyUserEmailReconcilePlanAsync(
                signingUserId,
                Arg.Any<UserEmailReconcilePlanCommand>(),
                Arg.Any<CancellationToken>())
            .Returns(new UserEmailReconcilePlanResult(
                new HashSet<Guid> { signingUserId, displacedUserId }));

        _inner.GetUserInfoAsync(signingUserId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfoWithEmail(signingUserId, "signing@example.com")));
        _inner.GetUserInfoAsync(displacedUserId, Arg.Any<CancellationToken>())
            .Returns(new ValueTask<UserInfo?>(UserInfoWithEmail(displacedUserId, "survivor@example.com")));

        await sut.ApplyUserEmailReconcilePlanAsync(
            signingUserId,
            new UserEmailReconcilePlanCommand(null, null, null, null));

        var signing = await sut.GetUserInfoAsync(signingUserId);
        var displaced = await sut.GetUserInfoAsync(displacedUserId);

        signing!.UserEmails.Should().ContainSingle(e => e.Email == "signing@example.com");
        displaced!.UserEmails.Should().ContainSingle(e => e.Email == "survivor@example.com");
    }

    [HumansFact]
    public async Task ReassignAsync_RefreshesBothAffectedUsers()
    {
        var sourceUserId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var sut = CreateSut();
        await PrimeAsync(sut, SampleUserInfo(sourceUserId, "Source"));
        await PrimeAsync(sut, SampleUserInfo(targetUserId, "Target"));

        var updatedAt = Instant.FromUtc(2026, 1, 2, 0, 0);
        _inner.ReassignAsync(
                sourceUserId,
                targetUserId,
                Arg.Any<Guid>(),
                updatedAt,
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        StubRefreshEntry(sourceUserId, new Profile
        {
            Id = Guid.NewGuid(),
            UserId = sourceUserId,
            BurnerName = "",
            FirstName = "Merged",
            LastName = "User",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            UpdatedAt = updatedAt,
        });
        StubRefreshEntry(targetUserId, new Profile
        {
            Id = Guid.NewGuid(),
            UserId = targetUserId,
            BurnerName = "Target Burner",
            FirstName = "Target",
            LastName = "Human",
            CreatedAt = Instant.FromUtc(2026, 1, 1, 0, 0),
            UpdatedAt = updatedAt,
        });

        await sut.ReassignAsync(sourceUserId, targetUserId, Guid.NewGuid(), updatedAt, CancellationToken.None);

        var source = await sut.GetUserInfoAsync(sourceUserId);
        var target = await sut.GetUserInfoAsync(targetUserId);
        source!.Profile.Should().NotBeNull();
        source.Profile!.FirstName.Should().Be("Merged");
        target!.Profile.Should().NotBeNull();
        target.Profile!.BurnerName.Should().Be("Target Burner");
    }
}
