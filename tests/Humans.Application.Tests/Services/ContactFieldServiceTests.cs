using System.ComponentModel.DataAnnotations;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;
using Humans.Infrastructure.Services;
using Xunit;

namespace Humans.Application.Tests.Services;

public class ContactFieldServiceTests : IDisposable
{
    private readonly HumansDbContext _dbContext;
    private readonly ITeamService _teamService;
    private readonly FakeClock _clock;
    private readonly ContactFieldService _service;

    public ContactFieldServiceTests()
    {
        var options = new DbContextOptionsBuilder<HumansDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _dbContext = new HumansDbContext(options);
        _teamService = Substitute.For<ITeamService>();
        _clock = new FakeClock(Instant.FromUtc(2024, 1, 15, 12, 0, 0));
        _service = new ContactFieldService(_dbContext, _teamService, _clock);
    }

    public void Dispose()
    {
        _dbContext.Dispose();
        GC.SuppressFinalize(this);
    }

    #region GetViewerAccessLevelAsync Tests

    [Fact]
    public async Task GetViewerAccessLevel_WhenSelf_ReturnsBoardOnly()
    {
        var userId = Guid.NewGuid();

        var result = await _service.GetViewerAccessLevelAsync(userId, userId);

        result.Should().Be(ContactFieldVisibility.BoardOnly);
    }

    [Fact]
    public async Task GetViewerAccessLevel_WhenBoardMember_ReturnsBoardOnly()
    {
        var ownerId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();
        _teamService.IsUserBoardMemberAsync(viewerId, Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _service.GetViewerAccessLevelAsync(ownerId, viewerId);

        result.Should().Be(ContactFieldVisibility.BoardOnly);
    }

    [Fact]
    public async Task GetViewerAccessLevel_WhenMetalead_ReturnsLeadsAndBoard()
    {
        var ownerId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();
        _teamService.IsUserBoardMemberAsync(viewerId, Arg.Any<CancellationToken>())
            .Returns(false);
        _teamService.GetUserTeamsAsync(viewerId, Arg.Any<CancellationToken>())
            .Returns(new List<TeamMember>
            {
                CreateTeamMember(viewerId, TeamMemberRole.Metalead)
            });
        _teamService.GetUserTeamsAsync(ownerId, Arg.Any<CancellationToken>())
            .Returns(new List<TeamMember>());

        var result = await _service.GetViewerAccessLevelAsync(ownerId, viewerId);

        result.Should().Be(ContactFieldVisibility.LeadsAndBoard);
    }

    [Fact]
    public async Task GetViewerAccessLevel_WhenSharesTeam_ReturnsMyTeams()
    {
        var ownerId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();
        var sharedTeamId = Guid.NewGuid();

        _teamService.IsUserBoardMemberAsync(viewerId, Arg.Any<CancellationToken>())
            .Returns(false);
        _teamService.GetUserTeamsAsync(viewerId, Arg.Any<CancellationToken>())
            .Returns(new List<TeamMember>
            {
                CreateTeamMember(viewerId, TeamMemberRole.Member, sharedTeamId)
            });
        _teamService.GetUserTeamsAsync(ownerId, Arg.Any<CancellationToken>())
            .Returns(new List<TeamMember>
            {
                CreateTeamMember(ownerId, TeamMemberRole.Member, sharedTeamId)
            });

        var result = await _service.GetViewerAccessLevelAsync(ownerId, viewerId);

        result.Should().Be(ContactFieldVisibility.MyTeams);
    }

    [Fact]
    public async Task GetViewerAccessLevel_WhenNoSharedTeams_ReturnsAllActiveProfiles()
    {
        var ownerId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();

        _teamService.IsUserBoardMemberAsync(viewerId, Arg.Any<CancellationToken>())
            .Returns(false);
        _teamService.GetUserTeamsAsync(viewerId, Arg.Any<CancellationToken>())
            .Returns(new List<TeamMember>
            {
                CreateTeamMember(viewerId, TeamMemberRole.Member, Guid.NewGuid())
            });
        _teamService.GetUserTeamsAsync(ownerId, Arg.Any<CancellationToken>())
            .Returns(new List<TeamMember>
            {
                CreateTeamMember(ownerId, TeamMemberRole.Member, Guid.NewGuid())
            });

        var result = await _service.GetViewerAccessLevelAsync(ownerId, viewerId);

        result.Should().Be(ContactFieldVisibility.AllActiveProfiles);
    }

    [Fact]
    public async Task GetViewerAccessLevel_WhenOnlySharesVolunteersTeam_ReturnsAllActiveProfiles()
    {
        // Volunteers team is excluded from "shared team" visibility
        var ownerId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();
        var volunteersTeamId = Guid.NewGuid();

        _teamService.IsUserBoardMemberAsync(viewerId, Arg.Any<CancellationToken>())
            .Returns(false);
        _teamService.GetUserTeamsAsync(viewerId, Arg.Any<CancellationToken>())
            .Returns(new List<TeamMember>
            {
                CreateTeamMember(viewerId, TeamMemberRole.Member, volunteersTeamId, SystemTeamType.Volunteers)
            });
        _teamService.GetUserTeamsAsync(ownerId, Arg.Any<CancellationToken>())
            .Returns(new List<TeamMember>
            {
                CreateTeamMember(ownerId, TeamMemberRole.Member, volunteersTeamId, SystemTeamType.Volunteers)
            });

        var result = await _service.GetViewerAccessLevelAsync(ownerId, viewerId);

        // Should NOT return MyTeams since Volunteers doesn't count
        result.Should().Be(ContactFieldVisibility.AllActiveProfiles);
    }

    [Fact]
    public async Task GetViewerAccessLevel_WhenSharesNonVolunteersTeam_ReturnsMyTeams()
    {
        // Sharing a non-Volunteers team should grant MyTeams visibility
        var ownerId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();
        var sharedTeamId = Guid.NewGuid();
        var volunteersTeamId = Guid.NewGuid();

        _teamService.IsUserBoardMemberAsync(viewerId, Arg.Any<CancellationToken>())
            .Returns(false);
        _teamService.GetUserTeamsAsync(viewerId, Arg.Any<CancellationToken>())
            .Returns(new List<TeamMember>
            {
                CreateTeamMember(viewerId, TeamMemberRole.Member, volunteersTeamId, SystemTeamType.Volunteers),
                CreateTeamMember(viewerId, TeamMemberRole.Member, sharedTeamId)
            });
        _teamService.GetUserTeamsAsync(ownerId, Arg.Any<CancellationToken>())
            .Returns(new List<TeamMember>
            {
                CreateTeamMember(ownerId, TeamMemberRole.Member, volunteersTeamId, SystemTeamType.Volunteers),
                CreateTeamMember(ownerId, TeamMemberRole.Member, sharedTeamId)
            });

        var result = await _service.GetViewerAccessLevelAsync(ownerId, viewerId);

        result.Should().Be(ContactFieldVisibility.MyTeams);
    }

    #endregion

    #region GetVisibleContactFieldsAsync Tests

    [Fact]
    public async Task GetVisibleContactFields_WhenProfileNotFound_ReturnsEmptyList()
    {
        var result = await _service.GetVisibleContactFieldsAsync(Guid.NewGuid(), Guid.NewGuid());

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task GetVisibleContactFields_FiltersFieldsByVisibility()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var viewerId = Guid.NewGuid();
        var profile = await CreateProfileWithFields(ownerId);

        // Viewer is just a regular member (no shared teams)
        _teamService.IsUserBoardMemberAsync(viewerId, Arg.Any<CancellationToken>())
            .Returns(false);
        _teamService.GetUserTeamsAsync(viewerId, Arg.Any<CancellationToken>())
            .Returns(new List<TeamMember>());
        _teamService.GetUserTeamsAsync(ownerId, Arg.Any<CancellationToken>())
            .Returns(new List<TeamMember>());

        // Act
        var result = await _service.GetVisibleContactFieldsAsync(profile.Id, viewerId);

        // Assert - should only see AllActiveProfiles field
        result.Should().HaveCount(1);
        result[0].Label.Should().Be("Email");
    }

    [Fact]
    public async Task GetVisibleContactFields_SelfSeesAllFields()
    {
        // Arrange
        var ownerId = Guid.NewGuid();
        var profile = await CreateProfileWithFields(ownerId);

        // Act
        var result = await _service.GetVisibleContactFieldsAsync(profile.Id, ownerId);

        // Assert - should see all 4 fields
        result.Should().HaveCount(4);
    }

    #endregion

    #region SaveContactFieldsAsync Tests

    [Fact]
    public async Task SaveContactFields_CreatesNewFields()
    {
        // Arrange
        var profile = await CreateProfile(Guid.NewGuid());
        var fields = new List<ContactFieldEditDto>
        {
            new(null, ContactFieldType.Email, null, "test@example.com", ContactFieldVisibility.AllActiveProfiles, 0),
            new(null, ContactFieldType.Signal, null, "+1234567890", ContactFieldVisibility.MyTeams, 1)
        };

        // Act
        await _service.SaveContactFieldsAsync(profile.Id, fields);

        // Assert
        var savedFields = await _dbContext.ContactFields
            .Where(cf => cf.ProfileId == profile.Id)
            .ToListAsync();
        savedFields.Should().HaveCount(2);
    }

    [Fact]
    public async Task SaveContactFields_UpdatesExistingFields()
    {
        // Arrange
        var profile = await CreateProfile(Guid.NewGuid());
        var existingField = new ContactField
        {
            Id = Guid.NewGuid(),
            ProfileId = profile.Id,
            FieldType = ContactFieldType.Email,
            Value = "old@example.com",
            Visibility = ContactFieldVisibility.AllActiveProfiles,
            DisplayOrder = 0,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.ContactFields.Add(existingField);
        await _dbContext.SaveChangesAsync();

        var fields = new List<ContactFieldEditDto>
        {
            new(existingField.Id, ContactFieldType.Email, null, "new@example.com", ContactFieldVisibility.BoardOnly, 0)
        };

        // Act
        await _service.SaveContactFieldsAsync(profile.Id, fields);

        // Assert
        var savedField = await _dbContext.ContactFields.FindAsync(existingField.Id);
        savedField!.Value.Should().Be("new@example.com");
        savedField.Visibility.Should().Be(ContactFieldVisibility.BoardOnly);
    }

    [Fact]
    public async Task SaveContactFields_DeletesRemovedFields()
    {
        // Arrange
        var profile = await CreateProfile(Guid.NewGuid());
        var existingField = new ContactField
        {
            Id = Guid.NewGuid(),
            ProfileId = profile.Id,
            FieldType = ContactFieldType.Email,
            Value = "old@example.com",
            Visibility = ContactFieldVisibility.AllActiveProfiles,
            DisplayOrder = 0,
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.ContactFields.Add(existingField);
        await _dbContext.SaveChangesAsync();

        // Save empty list (delete all)
        await _service.SaveContactFieldsAsync(profile.Id, new List<ContactFieldEditDto>());

        // Assert
        var remainingFields = await _dbContext.ContactFields
            .Where(cf => cf.ProfileId == profile.Id)
            .ToListAsync();
        remainingFields.Should().BeEmpty();
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("missing-at-sign")]
    [InlineData("@no-local-part.com")]
    public async Task SaveContactFields_WithInvalidEmail_ThrowsValidationException(string invalidEmail)
    {
        var profile = await CreateProfile(Guid.NewGuid());
        var fields = new List<ContactFieldEditDto>
        {
            new(null, ContactFieldType.Email, null, invalidEmail, ContactFieldVisibility.AllActiveProfiles, 0)
        };

        var act = () => _service.SaveContactFieldsAsync(profile.Id, fields);

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Theory]
    [InlineData("valid@example.com")]
    [InlineData("user.name+tag@domain.co")]
    public async Task SaveContactFields_WithValidEmail_Succeeds(string validEmail)
    {
        var profile = await CreateProfile(Guid.NewGuid());
        var fields = new List<ContactFieldEditDto>
        {
            new(null, ContactFieldType.Email, null, validEmail, ContactFieldVisibility.AllActiveProfiles, 0)
        };

        await _service.SaveContactFieldsAsync(profile.Id, fields);

        var savedFields = await _dbContext.ContactFields
            .Where(cf => cf.ProfileId == profile.Id)
            .ToListAsync();
        savedFields.Should().HaveCount(1);
        savedFields[0].Value.Should().Be(validEmail);
    }

    [Fact]
    public async Task SaveContactFields_WithInvalidValueOnNonEmailField_Succeeds()
    {
        // Non-email fields should not be validated as email
        var profile = await CreateProfile(Guid.NewGuid());
        var fields = new List<ContactFieldEditDto>
        {
            new(null, ContactFieldType.Telegram, null, "@my_telegram_handle", ContactFieldVisibility.AllActiveProfiles, 0)
        };

        await _service.SaveContactFieldsAsync(profile.Id, fields);

        var savedFields = await _dbContext.ContactFields
            .Where(cf => cf.ProfileId == profile.Id)
            .ToListAsync();
        savedFields.Should().HaveCount(1);
    }

    #endregion

    #region Helper Methods

    private TeamMember CreateTeamMember(Guid userId, TeamMemberRole role, Guid? teamId = null, SystemTeamType systemTeamType = SystemTeamType.None)
    {
        var actualTeamId = teamId ?? Guid.NewGuid();
        return new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = actualTeamId,
            UserId = userId,
            Role = role,
            JoinedAt = _clock.GetCurrentInstant(),
            Team = new Team
            {
                Id = actualTeamId,
                Name = "Test Team",
                Slug = "test-team",
                SystemTeamType = systemTeamType,
                CreatedAt = _clock.GetCurrentInstant(),
                UpdatedAt = _clock.GetCurrentInstant()
            }
        };
    }

    private async Task<Profile> CreateProfile(Guid userId)
    {
        var profile = new Profile
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            FirstName = "Test",
            LastName = "User",
            CreatedAt = _clock.GetCurrentInstant(),
            UpdatedAt = _clock.GetCurrentInstant()
        };
        _dbContext.Profiles.Add(profile);
        await _dbContext.SaveChangesAsync();
        return profile;
    }

    private async Task<Profile> CreateProfileWithFields(Guid userId)
    {
        var profile = await CreateProfile(userId);
        var now = _clock.GetCurrentInstant();

        var fields = new List<ContactField>
        {
            new()
            {
                Id = Guid.NewGuid(),
                ProfileId = profile.Id,
                FieldType = ContactFieldType.Email,
                Value = "public@example.com",
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
                Value = "@leadcontact",
                Visibility = ContactFieldVisibility.LeadsAndBoard,
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

        _dbContext.ContactFields.AddRange(fields);
        await _dbContext.SaveChangesAsync();

        return profile;
    }

    #endregion
}
