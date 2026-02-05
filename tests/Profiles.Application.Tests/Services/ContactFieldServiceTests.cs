using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using NodaTime;
using NodaTime.Testing;
using NSubstitute;
using Profiles.Application.DTOs;
using Profiles.Application.Interfaces;
using Profiles.Domain.Entities;
using Profiles.Domain.Enums;
using Profiles.Infrastructure.Data;
using Profiles.Infrastructure.Services;
using Xunit;

namespace Profiles.Application.Tests.Services;

public class ContactFieldServiceTests : IDisposable
{
    private readonly ProfilesDbContext _dbContext;
    private readonly ITeamService _teamService;
    private readonly FakeClock _clock;
    private readonly ContactFieldService _service;

    public ContactFieldServiceTests()
    {
        var options = new DbContextOptionsBuilder<ProfilesDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _dbContext = new ProfilesDbContext(options);
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

    #endregion

    #region Helper Methods

    private TeamMember CreateTeamMember(Guid userId, TeamMemberRole role, Guid? teamId = null)
    {
        return new TeamMember
        {
            Id = Guid.NewGuid(),
            TeamId = teamId ?? Guid.NewGuid(),
            UserId = userId,
            Role = role,
            JoinedAt = _clock.GetCurrentInstant()
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
