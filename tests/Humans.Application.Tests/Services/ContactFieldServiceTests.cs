using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NSubstitute;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Repositories;
using Humans.Application.Tests.Infrastructure;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using ContactFieldService = Humans.Application.Services.Profiles.ContactFieldService;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Users;
using Humans.Infrastructure.Repositories.Profiles;
using Microsoft.Extensions.Logging.Abstractions;

namespace Humans.Application.Tests.Services;

public sealed class ContactFieldServiceTests : ServiceTestHarness
{
    private readonly ITeamServiceRead _teamService;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly IProfileRepository _profileRepository;
    private readonly IUserService _userService;
    private readonly ContactFieldService _service;

    public ContactFieldServiceTests()
        : base(Instant.FromUtc(2024, 1, 15, 12, 0, 0))
    {
        _teamService = Substitute.For<ITeamServiceRead>();
        _roleAssignmentService = Substitute.For<IRoleAssignmentService>();
        _userService = Substitute.For<IUserService>();

        var repository = new ContactFieldRepository(DbFactory);
        _profileRepository = new ProfileRepository(DbFactory, Clock);

        _service = new ContactFieldService(
            repository, _profileRepository, _userService, _teamService, _roleAssignmentService,
            Substitute.For<IUserInfoInvalidator>(),
            Clock, NullLogger<ContactFieldService>.Instance);
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

    #region GetViewerAccessLevelAsync Tests

    [HumansFact]
    public async Task GetViewerAccessLevel_WhenSelf_ReturnsBoardOnly()
    {
        var userId = Guid.NewGuid();

        var result = await _service.GetViewerAccessLevelAsync(userId, userId);

        result.Should().Be(ContactFieldVisibility.BoardOnly);
    }

    [HumansFact]
    public async Task GetViewerAccessLevel_WhenBoardMember_ReturnsBoardOnly()
    {
        var ownerId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();
        _roleAssignmentService.IsUserBoardMemberAsync(viewerId, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _service.GetViewerAccessLevelAsync(ownerId, viewerId);

        result.Should().Be(ContactFieldVisibility.BoardOnly);
    }

    [HumansFact]
    public async Task GetViewerAccessLevel_WhenCoordinator_ReturnsCoordinatorsAndBoard()
    {
        var ownerId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();
        _roleAssignmentService.IsUserBoardMemberAsync(viewerId, Arg.Any<CancellationToken>())
            .Returns(false);
        SetupTeams((viewerId, TeamMemberRole.Coordinator, Guid.NewGuid(), SystemTeamType.None));

        var result = await _service.GetViewerAccessLevelAsync(ownerId, viewerId);

        result.Should().Be(ContactFieldVisibility.CoordinatorsAndBoard);
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

        var result = await _service.GetViewerAccessLevelAsync(ownerId, viewerId);

        result.Should().Be(ContactFieldVisibility.MyTeams);
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

        var result = await _service.GetViewerAccessLevelAsync(ownerId, viewerId);

        result.Should().Be(ContactFieldVisibility.AllActiveProfiles);
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

        var result = await _service.GetViewerAccessLevelAsync(ownerId, viewerId);

        // Should NOT return MyTeams since Volunteers doesn't count
        result.Should().Be(ContactFieldVisibility.AllActiveProfiles);
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

        var result = await _service.GetViewerAccessLevelAsync(ownerId, viewerId);

        result.Should().Be(ContactFieldVisibility.MyTeams);
    }

    #endregion

    #region GetVisibleContactFieldsAsync Tests

    [HumansFact]
    public async Task GetVisibleContactFields_WhenProfileNotFound_ReturnsEmptyList()
    {
        var result = await _service.GetVisibleContactFieldsAsync(Guid.NewGuid(), Guid.NewGuid());

        result.Should().BeEmpty();
    }

    [HumansFact]
    public async Task GetVisibleContactFields_FiltersFieldsByVisibility()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();
        var profile = await CreateProfileWithFields(ownerId);
        StubUserInfo(ownerId, profile);

        // Viewer is just a regular member (no shared teams)
        _roleAssignmentService.IsUserBoardMemberAsync(viewerId, Arg.Any<CancellationToken>())
            .Returns(false);
        SetupEmptyTeams();

        // Act
        var result = await _service.GetVisibleContactFieldsAsync(ownerId, viewerId);

        // Assert - should only see AllActiveProfiles field
        result.Should().HaveCount(1);
        result[0].Label.Should().Be("Phone");
    }

    [HumansFact]
    public async Task GetVisibleContactFields_SelfSeesAllFields()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var profile = await CreateProfileWithFields(ownerId);
        StubUserInfo(ownerId, profile);

        // Act
        var result = await _service.GetVisibleContactFieldsAsync(ownerId, ownerId);

        // Assert - should see all 4 fields
        result.Should().HaveCount(4);
    }

    #endregion

    #region SaveContactFieldsAsync Tests

    [HumansFact]
    public async Task SaveContactFields_CreatesNewFields()
    {
        // Arrange
        var profile = await CreateProfile(Guid.NewGuid());
        var fields = new List<ContactFieldEditDto>
        {
            new(null, ContactFieldType.Phone, null, "+34 612345678", ContactFieldVisibility.AllActiveProfiles, 0),
            new(null, ContactFieldType.Signal, null, "+1234567890", ContactFieldVisibility.MyTeams, 1)
        };

        // Act
        await _service.SaveContactFieldsAsync(profile.Id, fields);

        // Assert
        var savedFields = await Db.ContactFields
            .Where(cf => cf.ProfileId == profile.Id)
            .ToListAsync();
        savedFields.Should().HaveCount(2);
    }

    [HumansFact]
    public async Task SaveContactFields_UpdatesExistingFields()
    {
        // Arrange
        var profile = await CreateProfile(Guid.NewGuid());
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
        await Db.SaveChangesAsync();

        var fields = new List<ContactFieldEditDto>
        {
            new(existingField.Id, ContactFieldType.Phone, null, "+34 698765432", ContactFieldVisibility.BoardOnly, 0)
        };

        // Act
        await _service.SaveContactFieldsAsync(profile.Id, fields);

        // Assert
        var savedField = await Db.ContactFields.AsNoTracking()
            .FirstOrDefaultAsync(cf => cf.Id == existingField.Id);
        savedField!.Value.Should().Be("+34 698765432");
        savedField.Visibility.Should().Be(ContactFieldVisibility.BoardOnly);
    }

    [HumansFact(Timeout = 10000)]
    public async Task SaveContactFields_DeletesRemovedFields()
    {
        // Arrange
        var profile = await CreateProfile(Guid.NewGuid());
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
        await Db.SaveChangesAsync();

        // Save empty list (delete all)
        await _service.SaveContactFieldsAsync(profile.Id, new List<ContactFieldEditDto>());

        // Assert
        var remainingFields = await Db.ContactFields
            .Where(cf => cf.ProfileId == profile.Id)
            .ToListAsync();
        remainingFields.Should().BeEmpty();
    }

    [HumansFact]
    public async Task SaveContactFields_WithInvalidValueOnNonEmailField_Succeeds()
    {
        // Non-email fields should not be validated as email
        var profile = await CreateProfile(Guid.NewGuid());
        var fields = new List<ContactFieldEditDto>
        {
            new(null, ContactFieldType.Telegram, null, "@my_telegram_handle", ContactFieldVisibility.AllActiveProfiles, 0)
        };

        await _service.SaveContactFieldsAsync(profile.Id, fields);

        var savedFields = await Db.ContactFields
            .Where(cf => cf.ProfileId == profile.Id)
            .ToListAsync();
        savedFields.Should().HaveCount(1);
    }

    #endregion

    #region Helper Methods

    // Stub IUserService.GetUserInfoAsync to return a UserInfo carrying the profile's ContactFields.
    private void StubUserInfo(Guid userId, Profile profile)
    {
        var fields = Db.ContactFields
            .Where(cf => cf.ProfileId == profile.Id)
            .OrderBy(cf => cf.DisplayOrder)
            .ToList();
        var info = UserInfo.Create(
            user: new User
            {
                Id = userId,
                DisplayName = "",
                PreferredLanguage = "en",
                CreatedAt = profile.CreatedAt,
                GoogleEmailStatus = default,
            },
            userEmails: [],
            eventParticipations: [],
            externalLogins: [],
            profile: profile,
            contactFields: fields,
            profileLanguages: [],
            volunteerHistory: [],
            communicationPreferences: []);
        _userService.GetUserInfoAsync(userId, Arg.Any<CancellationToken>()).Returns(info);
    }

    private async Task<Profile> CreateProfile(Guid userId)
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
        await Db.SaveChangesAsync();

        // No store to populate — GetVisibleContactFieldsAsync now resolves profileId → userId
        // via IProfileRepository.GetOwnerUserIdAsync (scalar DB query).

        return profile;
    }

    private async Task<Profile> CreateProfileWithFields(Guid userId)
    {
        var profile = await CreateProfile(userId);
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
        await Db.SaveChangesAsync();

        return profile;
    }

    #endregion
}
