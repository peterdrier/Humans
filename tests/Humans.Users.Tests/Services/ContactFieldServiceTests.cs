using Humans.Email.Contracts;
using Xunit;
using Humans.Users.Data;
using Humans.Base.Extensions;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Options;
using Humans.Auth.Contracts;
using Humans.Users.Services;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using NSubstitute;
using Humans.Users.Tests.Infrastructure;
using Humans.Base.Enums;
using ContactFieldService = Humans.Users.Services.ContactFieldService;
using Humans.Teams.Contracts;
using Humans.Users.Data.Repositories;
using Microsoft.Extensions.Logging.Abstractions;
using Humans.Users.Contracts;

namespace Humans.Users.Tests.Services;

public sealed class ContactFieldServiceTests : ServiceTestHarness
{
    private readonly ITeamService _teamService;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly IUserManagement _userService;
    private readonly IUserInfoSliceRefresher _userInfoRefresher = Substitute.For<IUserInfoSliceRefresher>();
    private readonly IUserEmailService _emails = Substitute.For<IUserEmailService>();
    private readonly ICommunicationPreferenceService _preferences = Substitute.For<ICommunicationPreferenceService>();
    private readonly ContactFieldService _service;

    public ContactFieldServiceTests()
        : base(Instant.FromUtc(2024, 1, 15, 12, 0, 0))
    {
        _teamService = Substitute.For<ITeamService>();
        _roleAssignmentService = Substitute.For<IRoleAssignmentService>();
        _userService = Substitute.For<IUserManagement>();

        var repository = new UserRepository(DbFactory, Clock);

        _service = new ContactFieldService(
            repository, _userService, _teamService, _roleAssignmentService,
            _userInfoRefresher,
            Clock, NullLogger<ContactFieldService>.Instance,
            new StringLocalizer<UsersResource>(new ResourceManagerStringLocalizerFactory(
                Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance)), _emails, _preferences);
    }

    [HumansTheory]
    [InlineData(ContactFieldType.Phone)]
    [InlineData(ContactFieldType.WhatsApp)]
    public async Task SaveContactFields_InvalidPhone_RefusesWithoutReplacingExistingFields(ContactFieldType type)
    {
        var ct = TestContext.Current.CancellationToken;
        var profile = await CreateProfileAsync(Guid.NewGuid());
        var existing = new ContactField
        {
            Id = Guid.NewGuid(), ProfileId = profile.Id, FieldType = ContactFieldType.Phone,
            Value = "+34 612345678", Visibility = ContactFieldVisibility.BoardOnly,
            CreatedAt = Clock.GetCurrentInstant(), UpdatedAt = Clock.GetCurrentInstant()
        };
        Db.ContactFields.Add(existing);
        await Db.SaveChangesAsync(ct);

        var result = await _service.SaveContactFieldsAsync(profile.Id,
            [new(existing.Id, type, null, "612345678", ContactFieldVisibility.AllActiveProfiles, 0)], ct);

        result.Succeeded.Should().BeFalse();
        result.ErrorKey.Should().Be("Validation_PhoneE164");
        (await Db.ContactFields.AsNoTracking().SingleAsync(ct)).Value.Should().Be("+34 612345678");
    }

    [HumansTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SaveContactFields_PostCommitFailure_RefreshesCachedVisibility(bool lostAcknowledgement)
    {
        var ct = TestContext.Current.CancellationToken;
        var userId = Guid.NewGuid();
        var profile = await CreateProfileAsync(userId);
        var field = new ContactField
        {
            Id = Guid.NewGuid(),
            ProfileId = profile.Id,
            FieldType = ContactFieldType.Phone,
            Value = "+34 611111111",
            Visibility = ContactFieldVisibility.AllActiveProfiles,
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        };
        Db.ContactFields.Add(field);
        await Db.SaveChangesAsync(ct);

        async Task<UserInfo?> ReadInfoAsync(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            await using var read = await DbFactory.CreateDbContextAsync(token);
            return UserInfoFactory.Create(new User { Id = userId, PreferredLanguage = "en" }, [], [], profile,
                await read.ContactFields.AsNoTracking().ToListAsync(token), [], []);
        }
        _userService.GetAllUserInfosAsync(Arg.Any<CancellationToken>())
            .Returns(async call => (IReadOnlyCollection<UserInfo>)[(await ReadInfoAsync(call.Arg<CancellationToken>()))!]);
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>())
            .Returns(call => new ValueTask<UserInfo?>(ReadInfoAsync(call.Arg<CancellationToken>())));
        var services = new ServiceCollection();
        services.AddKeyedScoped<IUserManagement>(CachingUserService.InnerServiceKey, (_, _) => _userService);
        var cache = new CachingUserService(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            NullLogger<CachingUserService>.Instance);
        (await cache.GetUserInfoAsync(userId, ct))!.Profile!.ContactFields.Single().Visibility
            .Should().Be(ContactFieldVisibility.AllActiveProfiles);

        using var cancellation = new CancellationTokenSource();
        var failure = new IOException("Persistence acknowledgement failed");
        var options = new DbContextOptionsBuilder<UsersDbContext>(DbOptions)
            .AddInterceptors(new ContactSaveCompletion(cancellation, lostAcknowledgement ? failure : null)).Options;
        var service = new ContactFieldService(new UserRepository(new TestDbContextFactory(options), Clock),
            cache, _teamService, _roleAssignmentService, cache, Clock, NullLogger<ContactFieldService>.Instance,
            new StringLocalizer<UsersResource>(new ResourceManagerStringLocalizerFactory(
                Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance)), _emails, _preferences);
        var save = () => service.SaveContactFieldsAsync(profile.Id,
            [new(field.Id, field.FieldType, null, "+34 622222222", ContactFieldVisibility.BoardOnly, 0)], cancellation.Token);

        if (lostAcknowledgement)
            (await save.Should().ThrowAsync<IOException>()).Which.Should().BeSameAs(failure);
        else
            await save();

        cancellation.IsCancellationRequested.Should().BeTrue();
        (await Db.ContactFields.AsNoTracking().SingleAsync(ct)).Visibility.Should().Be(ContactFieldVisibility.BoardOnly);
        var cached = (await cache.GetUserInfoAsync(userId, ct))!.Profile!.ContactFields.Single();
        cached.Value.Should().Be("+34 622222222");
        cached.Visibility.Should().Be(ContactFieldVisibility.BoardOnly);
    }

    private sealed class ContactSaveCompletion(CancellationTokenSource cancellation, IOException? failure) : SaveChangesInterceptor
    {
        public override async ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData, int result, CancellationToken cancellationToken = default)
        {
            await cancellation.CancelAsync();
            if (failure is not null) throw failure;
            return result;
        }
    }

    // Sets up GetTeamsAsync to return a dict containing one TeamInfo per member spec.
    // Each spec is (userId, role, teamId, systemTeamType). Users on the same teamId share that team entry.
    private void SetupTeams(params (Guid UserId, TeamMemberRole Role, Guid TeamId, SystemTeamType SystemTeamType)[] memberSpecs)
    {
        var grouped = memberSpecs.GroupBy(s => s.TeamId);
        var dict = new Dictionary<Guid, TeamInfo>();
        foreach (var group in grouped)
        {
            var members = group
                .Select(s => new TeamMemberInfo(Guid.NewGuid(), s.UserId, "T", null, null, s.Role, Clock.GetCurrentInstant()))
                .ToList();
            var teamId = group.Key;
            var systemTeamType = group.First().SystemTeamType;
            dict[teamId] = new TeamInfo(
                Id: teamId, Name: "Test Team", Description: null, Slug: $"team-{teamId:N}",
                IsActive: true, IsSystemTeam: systemTeamType != SystemTeamType.None,
                SystemTeamType: systemTeamType, RequiresApproval: false,
                IsPublicPage: false, IsHidden: false, IsPromotedToDirectory: false,
                CreatedAt: Clock.GetCurrentInstant(), Members: members);
        }
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<Guid, TeamInfo>>(dict));
    }

    private void SetupEmptyTeams()
    {
        _teamService.GetTeamsAsync(Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<Guid, TeamInfo>>(
                new Dictionary<Guid, TeamInfo>()));
    }

    [HumansTheory]
    [InlineData(ProfileCardViewMode.Public, false, false, ContactFieldVisibility.AllActiveProfiles, false)]
    [InlineData(ProfileCardViewMode.Public, false, true, ContactFieldVisibility.BoardOnly, true)]
    [InlineData(ProfileCardViewMode.Public, true, false, ContactFieldVisibility.BoardOnly, false)]
    [InlineData(ProfileCardViewMode.Self, true, false, ContactFieldVisibility.BoardOnly, true)]
    [InlineData(ProfileCardViewMode.Admin, false, false, ContactFieldVisibility.BoardOnly, true)]
    public async Task ViewerAccess_PreservesPrivateFieldAndContactRulesForEachRenderingContext(
        ProfileCardViewMode mode, bool ownsProfile, bool isBoard,
        ContactFieldVisibility expectedScope, bool canViewLegalName)
    {
        var ownerId = Guid.NewGuid();
        var viewerId = ownsProfile ? ownerId : Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;
        SetupEmptyTeams();
        _roleAssignmentService.IsUserBoardMemberAsync(viewerId, ct).Returns(isBoard);
        _emails.GetVisibleEmailsAsync(ownerId, Arg.Any<ContactFieldVisibility>(), ct).Returns([]);
        _preferences.AcceptsFacilitatedMessagesAsync(ownerId, ct).Returns(true);

        var result = await _service.GetViewerAccessLevelAsync(ownerId, viewerId, ct, mode);

        result.AccessLevel.Should().Be(expectedScope);
        result.CanViewLegalName.Should().Be(canViewLegalName);
        result.CanSendMessage.Should().Be(mode != ProfileCardViewMode.Self);
        await _emails.Received(1).GetVisibleEmailsAsync(ownerId, expectedScope, ct);
    }

    [HumansTheory]
    [InlineData(ProfileCardViewMode.Public, true, true, false)]
    [InlineData(ProfileCardViewMode.Public, false, false, false)]
    [InlineData(ProfileCardViewMode.Public, false, true, true)]
    [InlineData(ProfileCardViewMode.Admin, true, true, false)]
    public async Task ViewerAccess_MessageEligibilityUsesPublishedAddressAndRecipientPreference(
        ProfileCardViewMode mode, bool publicAddress, bool acceptsMessages, bool expected)
    {
        var ownerId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();
        var ct = TestContext.Current.CancellationToken;
        _roleAssignmentService.IsUserBoardMemberAsync(viewerId, ct).Returns(true);
        _emails.GetVisibleEmailsAsync(ownerId, ContactFieldVisibility.BoardOnly, ct).Returns([
            new UserEmailDto(Guid.NewGuid(), "visible@example.com", true, null, null, true,
                publicAddress ? ContactFieldVisibility.AllActiveProfiles : ContactFieldVisibility.BoardOnly)
        ]);
        _preferences.AcceptsFacilitatedMessagesAsync(ownerId, ct).Returns(acceptsMessages);

        var result = await _service.GetViewerAccessLevelAsync(ownerId, viewerId, ct, mode);

        result.CanSendMessage.Should().Be(expected);
        if (publicAddress)
            await _preferences.DidNotReceive().AcceptsFacilitatedMessagesAsync(ownerId, Arg.Any<CancellationToken>());
    }

    [HumansTheory]
    [InlineData(ProfileCardViewMode.Self)]
    [InlineData(ProfileCardViewMode.Admin)]
    public async Task VisibleContacts_UsesFullScopeForAuthorizedSelfAndAdminHosts(ProfileCardViewMode mode)
    {
        var ownerId = Guid.NewGuid();
        StubUserInfo(ownerId, await CreateProfileWithFieldsAsync(ownerId));

        var fields = await _service.GetVisibleContactFieldsAsync(ownerId, Guid.NewGuid(), TestContext.Current.CancellationToken, mode);

        fields.Should().HaveCount(4);
    }

    #region GetViewerAccessLevelAsync Tests

    [HumansFact]
    public async Task GetViewerAccessLevel_WhenSelf_ReturnsBoardOnly()
    {
        var userId = Guid.NewGuid();

        var result = await _service.GetViewerAccessLevelAsync(userId, userId, TestContext.Current.CancellationToken);

        result.AccessLevel.Should().Be(ContactFieldVisibility.BoardOnly);
    }

    [HumansFact]
    public async Task GetViewerAccessLevel_WhenBoardMember_ReturnsBoardOnly()
    {
        var ownerId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();
        _roleAssignmentService.IsUserBoardMemberAsync(viewerId, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _service.GetViewerAccessLevelAsync(ownerId, viewerId, TestContext.Current.CancellationToken);

        result.AccessLevel.Should().Be(ContactFieldVisibility.BoardOnly);
    }

    [HumansFact]
    public async Task GetViewerAccessLevel_WhenCoordinator_ReturnsCoordinatorsAndBoard()
    {
        var ownerId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();
        _roleAssignmentService.IsUserBoardMemberAsync(viewerId, Arg.Any<CancellationToken>())
            .Returns(false);
        SetupTeams((viewerId, TeamMemberRole.Coordinator, Guid.NewGuid(), SystemTeamType.None));

        var result = await _service.GetViewerAccessLevelAsync(ownerId, viewerId, TestContext.Current.CancellationToken);

        result.AccessLevel.Should().Be(ContactFieldVisibility.CoordinatorsAndBoard);
    }

    [HumansFact]
    public async Task GetViewerAccessLevel_WhenSharesTeam_ReturnsMyTeams()
    {
        var ownerId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();
        var sharedTeamId = Guid.NewGuid();

        _roleAssignmentService.IsUserBoardMemberAsync(viewerId, Arg.Any<CancellationToken>())
            .Returns(false);
        SetupTeams(
            (viewerId, TeamMemberRole.Member, sharedTeamId, SystemTeamType.None),
            (ownerId, TeamMemberRole.Member, sharedTeamId, SystemTeamType.None));

        var result = await _service.GetViewerAccessLevelAsync(ownerId, viewerId, TestContext.Current.CancellationToken);

        result.AccessLevel.Should().Be(ContactFieldVisibility.MyTeams);
    }

    [HumansFact]
    public async Task GetViewerAccessLevel_WhenNoSharedTeams_ReturnsAllActiveProfiles()
    {
        var ownerId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();

        _roleAssignmentService.IsUserBoardMemberAsync(viewerId, Arg.Any<CancellationToken>())
            .Returns(false);
        SetupTeams(
            (viewerId, TeamMemberRole.Member, Guid.NewGuid(), SystemTeamType.None),
            (ownerId, TeamMemberRole.Member, Guid.NewGuid(), SystemTeamType.None));

        var result = await _service.GetViewerAccessLevelAsync(ownerId, viewerId, TestContext.Current.CancellationToken);

        result.AccessLevel.Should().Be(ContactFieldVisibility.AllActiveProfiles);
    }

    [HumansFact]
    public async Task GetViewerAccessLevel_WhenOnlySharesVolunteersTeam_ReturnsAllActiveProfiles()
    {
        // Volunteers team is excluded from "shared team" visibility
        var ownerId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();
        var volunteersTeamId = Guid.NewGuid();

        _roleAssignmentService.IsUserBoardMemberAsync(viewerId, Arg.Any<CancellationToken>())
            .Returns(false);
        SetupTeams(
            (viewerId, TeamMemberRole.Member, volunteersTeamId, SystemTeamType.Volunteers),
            (ownerId, TeamMemberRole.Member, volunteersTeamId, SystemTeamType.Volunteers));

        var result = await _service.GetViewerAccessLevelAsync(ownerId, viewerId, TestContext.Current.CancellationToken);

        // Should NOT return MyTeams since Volunteers doesn't count
        result.AccessLevel.Should().Be(ContactFieldVisibility.AllActiveProfiles);
    }

    [HumansFact]
    public async Task GetViewerAccessLevel_WhenSharesNonVolunteersTeam_ReturnsMyTeams()
    {
        // Sharing a non-Volunteers team should grant MyTeams visibility
        var ownerId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();
        var sharedTeamId = Guid.NewGuid();
        var volunteersTeamId = Guid.NewGuid();

        _roleAssignmentService.IsUserBoardMemberAsync(viewerId, Arg.Any<CancellationToken>())
            .Returns(false);
        SetupTeams(
            (viewerId, TeamMemberRole.Member, volunteersTeamId, SystemTeamType.Volunteers),
            (viewerId, TeamMemberRole.Member, sharedTeamId, SystemTeamType.None),
            (ownerId, TeamMemberRole.Member, volunteersTeamId, SystemTeamType.Volunteers),
            (ownerId, TeamMemberRole.Member, sharedTeamId, SystemTeamType.None));

        var result = await _service.GetViewerAccessLevelAsync(ownerId, viewerId, TestContext.Current.CancellationToken);

        result.AccessLevel.Should().Be(ContactFieldVisibility.MyTeams);
    }

    #endregion

    #region GetVisibleContactFieldsAsync Tests

    [HumansFact]
    public async Task GetVisibleContactFields_WhenProfileNotFound_ReturnsEmptyList()
    {
        var result = await _service.GetVisibleContactFieldsAsync(Guid.NewGuid(), Guid.NewGuid(), TestContext.Current.CancellationToken);

        result.Should().BeEmpty();
    }

    [HumansTheory]
    [InlineData(ContactFieldVisibility.BoardOnly, true, 4)]
    [InlineData(ContactFieldVisibility.BoardOnly, false, 4)]
    [InlineData(ContactFieldVisibility.CoordinatorsAndBoard, true, 3)]
    [InlineData(ContactFieldVisibility.CoordinatorsAndBoard, false, 3)]
    [InlineData(ContactFieldVisibility.MyTeams, true, 2)]
    [InlineData(ContactFieldVisibility.MyTeams, false, 2)]
    public async Task GetVisibleContactFields_UsesEachViewersPermissionsOnTheSameService(
        ContactFieldVisibility privilege, bool privilegedFirst, int privilegedFieldCount)
    {
        var ownerId = Guid.NewGuid();
        var privilegedViewerId = Guid.NewGuid();
        var ordinaryViewerId = Guid.NewGuid();
        StubUserInfo(ownerId, await CreateProfileWithFieldsAsync(ownerId));
        var teamId = Guid.NewGuid();
        if (privilege == ContactFieldVisibility.BoardOnly)
        {
            _roleAssignmentService.IsUserBoardMemberAsync(privilegedViewerId, Arg.Any<CancellationToken>())
                .Returns(true);
            SetupEmptyTeams();
        }
        else if (privilege == ContactFieldVisibility.CoordinatorsAndBoard)
        {
            SetupTeams((privilegedViewerId, TeamMemberRole.Coordinator, teamId, SystemTeamType.None));
        }
        else
        {
            SetupTeams(
                (privilegedViewerId, TeamMemberRole.Member, teamId, SystemTeamType.None),
                (ownerId, TeamMemberRole.Member, teamId, SystemTeamType.None));
        }

        var firstViewerId = privilegedFirst ? privilegedViewerId : ordinaryViewerId;
        var secondViewerId = privilegedFirst ? ordinaryViewerId : privilegedViewerId;
        var firstFields = await _service.GetVisibleContactFieldsAsync(
            ownerId, firstViewerId, TestContext.Current.CancellationToken);
        var secondFields = await _service.GetVisibleContactFieldsAsync(
            ownerId, secondViewerId, TestContext.Current.CancellationToken);

        var privilegedFields = privilegedFirst ? firstFields : secondFields;
        var ordinaryFields = privilegedFirst ? secondFields : firstFields;
        privilegedFields.Should().HaveCount(privilegedFieldCount);
        ordinaryFields.Should().ContainSingle().Which.Visibility.Should().Be(ContactFieldVisibility.AllActiveProfiles);
    }

    [HumansFact]
    public async Task GetVisibleContactFields_FiltersFieldsByVisibility()
    {
        var ownerId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();
        var profile = await CreateProfileWithFieldsAsync(ownerId);
        StubUserInfo(ownerId, profile);

        // Viewer is just a regular member (no shared teams)
        _roleAssignmentService.IsUserBoardMemberAsync(viewerId, Arg.Any<CancellationToken>())
            .Returns(false);
        SetupEmptyTeams();

        var result = await _service.GetVisibleContactFieldsAsync(ownerId, viewerId, TestContext.Current.CancellationToken);

        result.Should().HaveCount(1);
        result[0].Label.Should().Be("Phone");
    }

    [HumansFact]
    public async Task GetVisibleContactFields_LocalizesDefaultLabelsAndPreservesCustomLabels()
    {
        using var cultureScope = new CultureScope("en");
        var localizer = new StringLocalizer<UsersResource>(new ResourceManagerStringLocalizerFactory(
            Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance));
        var ownerId = Guid.NewGuid();
        var profile = await CreateProfileAsync(ownerId);
        foreach (var type in Enum.GetValues<ContactFieldType>())
        {
            Db.ContactFields.Add(new ContactField
            {
                Id = Guid.NewGuid(),
                ProfileId = profile.Id,
                FieldType = type,
                Value = "Contact value",
                Visibility = ContactFieldVisibility.AllActiveProfiles,
                CreatedAt = Clock.GetCurrentInstant(),
                UpdatedAt = Clock.GetCurrentInstant(),
            });
        }
        Db.ContactFields.Add(new ContactField
        {
            Id = Guid.NewGuid(),
            ProfileId = profile.Id,
            FieldType = ContactFieldType.Other,
            CustomLabel = "My custom label",
            Value = "Custom value",
            Visibility = ContactFieldVisibility.AllActiveProfiles,
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant(),
        });
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        StubUserInfo(ownerId, profile);
        var labels = await _service.GetVisibleContactFieldsAsync(ownerId, ownerId, TestContext.Current.CancellationToken);
        labels.Single(f => f.FieldType == ContactFieldType.Phone).Label.Should().Be(localizer.EnumDisplay(ContactFieldType.Phone));
        foreach (var field in labels.Where(f => !string.Equals(f.Value, "Custom value", StringComparison.Ordinal)))
        {
            var expected = localizer[$"Enum_ContactFieldType_{field.FieldType}"];
            expected.ResourceNotFound.Should().BeFalse();
            field.Label.Should().Be(expected.Value);
        }
        labels.Single(f => string.Equals(f.Value, "Custom value", StringComparison.Ordinal)).Label.Should().Be("My custom label");
    }

    [HumansFact]
    public async Task GetVisibleContactFields_SelfSeesAllFields()
    {
        var ownerId = Guid.NewGuid();
        var profile = await CreateProfileWithFieldsAsync(ownerId);
        StubUserInfo(ownerId, profile);

        var result = await _service.GetVisibleContactFieldsAsync(ownerId, ownerId, TestContext.Current.CancellationToken);

        result.Should().HaveCount(4);
        result.ToDictionary(f => f.Id, f => f.DisplayOrder).Should().BeEquivalentTo(
            Db.ContactFields.Where(f => f.ProfileId == profile.Id).ToDictionary(f => f.Id, f => f.DisplayOrder));
    }

    #endregion

    #region SaveContactFieldsAsync Tests

    [HumansFact]
    public async Task SaveContactFields_CreatesNewFields()
    {
        var profile = await CreateProfileAsync(Guid.NewGuid());
        var fields = new List<ContactFieldEditDto>
        {
            new(null, ContactFieldType.Phone, null, "+34 612345678", ContactFieldVisibility.AllActiveProfiles, 0),
            new(null, ContactFieldType.Signal, null, "+1234567890", ContactFieldVisibility.MyTeams, 1)
        };

        await _service.SaveContactFieldsAsync(profile.Id, fields, TestContext.Current.CancellationToken);

        var savedFields = await Db.ContactFields
            .Where(cf => cf.ProfileId == profile.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        savedFields.Should().HaveCount(2);
    }

    [HumansFact]
    public async Task SaveContactFields_RefreshesTheOwnersUserInfo()
    {
        // contact_fields sit outside the SaveChanges interceptor's reach, so the service
        // refreshes the owner's UserInfo itself; callers (dev seeding) rely on it.
        var ct = TestContext.Current.CancellationToken;
        var userId = Guid.NewGuid();
        var profile = await CreateProfileAsync(userId);

        await _service.SaveContactFieldsAsync(profile.Id,
            [new(null, ContactFieldType.Signal, null, "+1234567890", ContactFieldVisibility.MyTeams, 0)], ct);

        await _userInfoRefresher.Received(1).RefreshAsync(
            userId, Arg.Any<CancellationToken>(), Arg.Any<string>(), Arg.Any<string>());
    }

    [HumansFact]
    public async Task SaveContactFields_UpdatesExistingFields()
    {
        var profile = await CreateProfileAsync(Guid.NewGuid());
        var existingField = new ContactField
        {
            Id = Guid.NewGuid(),
            ProfileId = profile.Id,
            FieldType = ContactFieldType.Phone,
            Value = "+34 612345678",
            Visibility = ContactFieldVisibility.AllActiveProfiles,
            DisplayOrder = 0,
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        };
        Db.ContactFields.Add(existingField);
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        var fields = new List<ContactFieldEditDto>
        {
            new(existingField.Id, ContactFieldType.Phone, null, "+34 698765432", ContactFieldVisibility.BoardOnly, 0)
        };

        await _service.SaveContactFieldsAsync(profile.Id, fields, TestContext.Current.CancellationToken);

        var savedField = await Db.ContactFields.AsNoTracking()
            .FirstOrDefaultAsync(cf => cf.Id == existingField.Id, TestContext.Current.CancellationToken);
        savedField!.Value.Should().Be("+34 698765432");
        savedField.Visibility.Should().Be(ContactFieldVisibility.BoardOnly);
    }

    [HumansFact(Timeout = 10000)]
    public async Task SaveContactFields_DeletesRemovedFields()
    {
        var profile = await CreateProfileAsync(Guid.NewGuid());
        var existingField = new ContactField
        {
            Id = Guid.NewGuid(),
            ProfileId = profile.Id,
            FieldType = ContactFieldType.Phone,
            Value = "+34 612345678",
            Visibility = ContactFieldVisibility.AllActiveProfiles,
            DisplayOrder = 0,
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        };
        Db.ContactFields.Add(existingField);
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        // Save empty list (delete all)
        await _service.SaveContactFieldsAsync(profile.Id, new List<ContactFieldEditDto>(), TestContext.Current.CancellationToken);

        var remainingFields = await Db.ContactFields
            .Where(cf => cf.ProfileId == profile.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        remainingFields.Should().BeEmpty();
    }

    [HumansFact]
    public async Task SaveContactFields_Omitted_legacy_email_is_preserved()
    {
        var ct = TestContext.Current.CancellationToken;
        var profile = await CreateProfileAsync(Guid.NewGuid());
        var legacy = new ContactField
        {
            Id = Guid.NewGuid(),
            ProfileId = profile.Id,
            FieldType = default,
            Value = "legacy@example.org",
            Visibility = ContactFieldVisibility.BoardOnly,
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        };
        Db.ContactFields.Add(legacy);
        await Db.SaveChangesAsync(ct);

        await _service.SaveContactFieldsAsync(profile.Id, [], ct);

        var saved = await Db.ContactFields.AsNoTracking().SingleAsync(ct);
        saved.Id.Should().Be(legacy.Id);
        saved.FieldType.Should().Be(default);
        saved.Value.Should().Be("legacy@example.org");
        saved.Visibility.Should().Be(ContactFieldVisibility.BoardOnly);
    }

    [HumansFact]
    public async Task Profile_editor_keeps_legacy_email_out_of_editable_type_selector()
    {
        var userId = Guid.NewGuid();
        var profile = await CreateProfileAsync(userId);
        var legacyId = Guid.NewGuid();
        var signalId = Guid.NewGuid();
        await Db.ContactFields.AddRangeAsync(
            new ContactField
            {
                Id = legacyId,
                ProfileId = profile.Id,
                FieldType = default,
                Value = "legacy@example.org",
                Visibility = ContactFieldVisibility.BoardOnly,
                CreatedAt = Clock.GetCurrentInstant(),
                UpdatedAt = Clock.GetCurrentInstant()
            },
            new ContactField
            {
                Id = signalId,
                ProfileId = profile.Id,
                FieldType = ContactFieldType.Signal,
                Value = "signal-handle",
                Visibility = ContactFieldVisibility.MyTeams,
                CreatedAt = Clock.GetCurrentInstant(),
                UpdatedAt = Clock.GetCurrentInstant()
            });
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);
        StubUserInfo(userId, profile);
        var info = await _userService.GetUserInfoAsync(userId);

        var model = Humans.Users.Models.ProfileEditViewModelBuilder.Build(info!, [], false);

        model.EditableContactFields.Should().ContainSingle().Which.Id.Should().Be(signalId);
        model.ContactFields.Should().ContainSingle().Which.Id.Should().Be(legacyId);
        model.ContactFields[0].Value.Should().Be("legacy@example.org");
        model.ContactFields[0].Visibility.Should().Be(ContactFieldVisibility.BoardOnly);
    }

    [HumansTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Legacy_contact_removal_is_explicit_and_profile_scoped(bool removeOwn, bool staleEditor)
    {
        var ct = TestContext.Current.CancellationToken;
        var profile = await CreateProfileAsync(Guid.NewGuid());
        var otherProfile = await CreateProfileAsync(Guid.NewGuid());
        var now = Clock.GetCurrentInstant();
        var legacy = new ContactField
        {
            Id = Guid.NewGuid(),
            ProfileId = profile.Id,
            FieldType = default,
            Value = "legacy@example.org",
            Visibility = ContactFieldVisibility.BoardOnly,
            CreatedAt = now,
            UpdatedAt = now
        };
        var phone = new ContactField
        {
            Id = Guid.NewGuid(),
            ProfileId = profile.Id,
            FieldType = ContactFieldType.Phone,
            Value = "old-number",
            Visibility = ContactFieldVisibility.MyTeams,
            CreatedAt = now,
            UpdatedAt = now
        };
        var foreign = new ContactField
        {
            Id = Guid.NewGuid(),
            ProfileId = otherProfile.Id,
            FieldType = default,
            Value = "other@example.org",
            Visibility = ContactFieldVisibility.BoardOnly,
            CreatedAt = now,
            UpdatedAt = now
        };
        await Db.ContactFields.AddRangeAsync(legacy, phone, foreign);
        await Db.SaveChangesAsync(ct);
        var fields = new List<ContactFieldEditDto>
        {
            new(phone.Id, ContactFieldType.Phone, null, "+34 698765432", ContactFieldVisibility.MyTeams, 0)
        };
        if (staleEditor)
            fields.Add(new(legacy.Id, ContactFieldType.Phone, null, "+34 612345678", ContactFieldVisibility.AllActiveProfiles, 1));

        await _service.SaveContactFieldsAsync(profile.Id, fields, ct,
            [removeOwn ? legacy.Id : foreign.Id, phone.Id]);

        var rows = await Db.ContactFields.AsNoTracking().ToListAsync(ct);
        rows.Single(cf => cf.Id == foreign.Id).Value.Should().Be("other@example.org");
        rows.Single(cf => cf.Id == phone.Id).Value.Should().Be("+34 698765432");
        if (removeOwn) rows.Should().NotContain(cf => cf.Id == legacy.Id);
        else
        {
            var saved = rows.Single(cf => cf.Id == legacy.Id);
            saved.FieldType.Should().Be(default);
            saved.Value.Should().Be("legacy@example.org");
            saved.Visibility.Should().Be(ContactFieldVisibility.BoardOnly);
        }
    }

    [HumansFact]
    public async Task SaveContactFields_OwnerLookupFailure_DoesNotCommitWithoutCacheRefresh()
    {
        var ct = TestContext.Current.CancellationToken;
        var profile = await CreateProfileAsync(Guid.NewGuid());
        var field = new ContactField
        {
            Id = Guid.NewGuid(),
            ProfileId = profile.Id,
            FieldType = ContactFieldType.Phone,
            Value = "+34 612345678",
            Visibility = ContactFieldVisibility.AllActiveProfiles,
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant(),
        };
        Db.ContactFields.Add(field);
        await Db.SaveChangesAsync(ct);
        var inner = new UserRepository(DbFactory, Clock);
        var repository = Substitute.For<IUserRepository>();
        repository.GetByProfileIdForMutationAsync(profile.Id, Arg.Any<CancellationToken>())
            .Returns(c => inner.GetByProfileIdForMutationAsync(profile.Id, c.Arg<CancellationToken>()));
        repository.BatchSaveAsync(Arg.Any<IReadOnlyList<ContactField>>(),
                Arg.Any<IReadOnlyList<ContactField>>(), Arg.Any<IReadOnlyList<ContactField>>(),
                Arg.Any<CancellationToken>())
            .Returns(c => inner.BatchSaveAsync(c.ArgAt<IReadOnlyList<ContactField>>(0),
                c.ArgAt<IReadOnlyList<ContactField>>(1), c.ArgAt<IReadOnlyList<ContactField>>(2),
                c.Arg<CancellationToken>()));
        repository.GetOwnerUserIdAsync(profile.Id, Arg.Any<CancellationToken>())
            .Returns(Task.FromException<Guid?>(new InvalidOperationException("Owner lookup failed")));
        var service = new ContactFieldService(
            repository, _userService, _teamService, _roleAssignmentService,
            Substitute.For<IUserInfoSliceRefresher>(), Clock, NullLogger<ContactFieldService>.Instance,
            new StringLocalizer<UsersResource>(new ResourceManagerStringLocalizerFactory(
                Options.Create(new LocalizationOptions()), NullLoggerFactory.Instance)), _emails, _preferences);

        var act = () => service.SaveContactFieldsAsync(profile.Id,
            [new(field.Id, field.FieldType, null, field.Value, ContactFieldVisibility.BoardOnly, 0)], ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("Owner lookup failed");
        (await Db.ContactFields.AsNoTracking().SingleAsync(ct)).Visibility
            .Should().Be(ContactFieldVisibility.AllActiveProfiles);
    }

    [HumansFact]
    public async Task SaveContactFields_WithInvalidValueOnNonEmailField_Succeeds()
    {
        // Non-email fields should not be validated as email
        var profile = await CreateProfileAsync(Guid.NewGuid());
        var fields = new List<ContactFieldEditDto>
        {
            new(null, ContactFieldType.Telegram, null, "@my_telegram_handle", ContactFieldVisibility.AllActiveProfiles, 0)
        };

        await _service.SaveContactFieldsAsync(profile.Id, fields, TestContext.Current.CancellationToken);

        var savedFields = await Db.ContactFields
            .Where(cf => cf.ProfileId == profile.Id)
            .ToListAsync(TestContext.Current.CancellationToken);
        savedFields.Should().HaveCount(1);
    }

    #endregion

    #region Helper Methods

    // Stub IUserManagement.GetUserInfoAsync to return a UserInfo carrying the profile's ContactFields.
    private void StubUserInfo(Guid userId, Profile profile)
    {
        var fields = Db.ContactFields
            .Where(cf => cf.ProfileId == profile.Id)
            .OrderBy(cf => cf.DisplayOrder)
            .ToList();
        var info = UserInfoFactory.Create(
            user: new User
            {
                Id = userId,
                DisplayName = "",
                PreferredLanguage = "en",
                CreatedAt = profile.CreatedAt,
            },
            userEmails: [],
            externalLogins: [],
            profile: profile,
            contactFields: fields,
            profileLanguages: [],
            volunteerHistory: []);
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(info);
    }

    private async Task<Profile> CreateProfileAsync(Guid userId)
    {
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            FirstName = "Test",
            LastName = "User",
            CreatedAt = Clock.GetCurrentInstant(),
            UpdatedAt = Clock.GetCurrentInstant()
        };
        Db.Profiles.Add(profile);
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return profile;
    }

    private async Task<Profile> CreateProfileWithFieldsAsync(Guid userId)
    {
        var profile = await CreateProfileAsync(userId);
        var now = Clock.GetCurrentInstant();

        var fields = new List<ContactField>
        {
            new()
            {
                Id = Guid.NewGuid(),
                ProfileId = profile.Id,
                FieldType = ContactFieldType.Phone,
                Value = "+34 612345678",
                Visibility = ContactFieldVisibility.AllActiveProfiles,
                DisplayOrder = 0,
                CreatedAt = now,
                UpdatedAt = now
            },
            new()
            {
                Id = Guid.NewGuid(),
                ProfileId = profile.Id,
                FieldType = ContactFieldType.Signal,
                Value = "+1234567890",
                Visibility = ContactFieldVisibility.MyTeams,
                DisplayOrder = 1,
                CreatedAt = now,
                UpdatedAt = now
            },
            new()
            {
                Id = Guid.NewGuid(),
                ProfileId = profile.Id,
                FieldType = ContactFieldType.Telegram,
                Value = "@coordcontact",
                Visibility = ContactFieldVisibility.CoordinatorsAndBoard,
                DisplayOrder = 2,
                CreatedAt = now,
                UpdatedAt = now
            },
            new()
            {
                Id = Guid.NewGuid(),
                ProfileId = profile.Id,
                FieldType = ContactFieldType.Phone,
                Value = "+9876543210",
                Visibility = ContactFieldVisibility.BoardOnly,
                DisplayOrder = 3,
                CreatedAt = now,
                UpdatedAt = now
            }
        };

        Db.ContactFields.AddRange(fields);
        await Db.SaveChangesAsync(TestContext.Current.CancellationToken);

        return profile;
    }

    #endregion
}
