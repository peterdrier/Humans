using Microsoft.EntityFrameworkCore;
using NodaTime;
using Profiles.Application.DTOs;
using Profiles.Application.Interfaces;
using Profiles.Domain.Entities;
using Profiles.Domain.Enums;
using Profiles.Infrastructure.Data;

namespace Profiles.Infrastructure.Services;

/// <summary>
/// Service for managing contact fields with visibility controls.
/// </summary>
public class ContactFieldService : IContactFieldService
{
    private readonly ProfilesDbContext _dbContext;
    private readonly ITeamService _teamService;
    private readonly IClock _clock;

    public ContactFieldService(
        ProfilesDbContext dbContext,
        ITeamService teamService,
        IClock clock)
    {
        _dbContext = dbContext;
        _teamService = teamService;
        _clock = clock;
    }

    public async Task<IReadOnlyList<ContactFieldDto>> GetVisibleContactFieldsAsync(
        Guid profileId,
        Guid viewerUserId,
        CancellationToken cancellationToken = default)
    {
        // Get the profile owner's user ID
        var profile = await _dbContext.Profiles
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == profileId, cancellationToken);

        if (profile == null)
        {
            return [];
        }

        var accessLevel = await GetViewerAccessLevelAsync(profile.UserId, viewerUserId, cancellationToken);

        // Filter fields by visibility - lower or equal visibility values are more restrictive,
        // so a viewer with access level X can see fields with visibility >= X
        var fields = await _dbContext.ContactFields
            .AsNoTracking()
            .Where(cf => cf.ProfileId == profileId && cf.Visibility >= accessLevel)
            .OrderBy(cf => cf.DisplayOrder)
            .ThenBy(cf => cf.CreatedAt)
            .ToListAsync(cancellationToken);

        return fields.Select(cf => new ContactFieldDto(
            cf.Id,
            cf.FieldType,
            cf.DisplayLabel,
            cf.Value,
            cf.Visibility
        )).ToList();
    }

    public async Task<IReadOnlyList<ContactFieldEditDto>> GetAllContactFieldsAsync(
        Guid profileId,
        CancellationToken cancellationToken = default)
    {
        var fields = await _dbContext.ContactFields
            .AsNoTracking()
            .Where(cf => cf.ProfileId == profileId)
            .OrderBy(cf => cf.DisplayOrder)
            .ThenBy(cf => cf.CreatedAt)
            .ToListAsync(cancellationToken);

        return fields.Select(cf => new ContactFieldEditDto(
            cf.Id,
            cf.FieldType,
            cf.CustomLabel,
            cf.Value,
            cf.Visibility,
            cf.DisplayOrder
        )).ToList();
    }

    public async Task SaveContactFieldsAsync(
        Guid profileId,
        IReadOnlyList<ContactFieldEditDto> fields,
        CancellationToken cancellationToken = default)
    {
        var now = _clock.GetCurrentInstant();

        // Get existing fields
        var existingFields = await _dbContext.ContactFields
            .Where(cf => cf.ProfileId == profileId)
            .ToListAsync(cancellationToken);

        var existingById = existingFields.ToDictionary(cf => cf.Id);
        var incomingIds = fields.Where(f => f.Id.HasValue).Select(f => f.Id!.Value).ToHashSet();

        // Delete fields that are no longer present
        var toDelete = existingFields.Where(cf => !incomingIds.Contains(cf.Id)).ToList();
        _dbContext.ContactFields.RemoveRange(toDelete);

        // Update or create fields
        foreach (var dto in fields)
        {
            if (dto.Id.HasValue && existingById.TryGetValue(dto.Id.Value, out var existing))
            {
                // Update existing
                existing.FieldType = dto.FieldType;
                existing.CustomLabel = dto.CustomLabel;
                existing.Value = dto.Value;
                existing.Visibility = dto.Visibility;
                existing.DisplayOrder = dto.DisplayOrder;
                existing.UpdatedAt = now;
            }
            else
            {
                // Create new
                var newField = new ContactField
                {
                    Id = Guid.NewGuid(),
                    ProfileId = profileId,
                    FieldType = dto.FieldType,
                    CustomLabel = dto.CustomLabel,
                    Value = dto.Value,
                    Visibility = dto.Visibility,
                    DisplayOrder = dto.DisplayOrder,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                _dbContext.ContactFields.Add(newField);
            }
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task<ContactFieldVisibility> GetViewerAccessLevelAsync(
        Guid ownerUserId,
        Guid viewerUserId,
        CancellationToken cancellationToken = default)
    {
        // Self viewing - can see everything (return most permissive access level)
        if (ownerUserId == viewerUserId)
        {
            return ContactFieldVisibility.BoardOnly;
        }

        // Board member - can see everything
        var isBoardMember = await _teamService.IsUserBoardMemberAsync(viewerUserId, cancellationToken);
        if (isBoardMember)
        {
            return ContactFieldVisibility.BoardOnly;
        }

        // Check if viewer is a metalead of any team
        var viewerTeams = await _teamService.GetUserTeamsAsync(viewerUserId, cancellationToken);
        var isAnyMetalead = viewerTeams.Any(tm => tm.Role == TeamMemberRole.Metalead);
        if (isAnyMetalead)
        {
            return ContactFieldVisibility.LeadsAndBoard;
        }

        // Check if viewer shares any team with owner (excluding Volunteers since everyone is in it)
        var ownerTeams = await _teamService.GetUserTeamsAsync(ownerUserId, cancellationToken);
        var viewerTeamIds = viewerTeams
            .Where(tm => tm.Team.SystemTeamType != SystemTeamType.Volunteers)
            .Select(tm => tm.TeamId)
            .ToHashSet();
        var ownerTeamIds = ownerTeams
            .Where(tm => tm.Team.SystemTeamType != SystemTeamType.Volunteers)
            .Select(tm => tm.TeamId)
            .ToHashSet();
        var sharesTeam = viewerTeamIds.Intersect(ownerTeamIds).Any();
        if (sharesTeam)
        {
            return ContactFieldVisibility.MyTeams;
        }

        // Default: can only see AllActiveProfiles visibility
        return ContactFieldVisibility.AllActiveProfiles;
    }
}
