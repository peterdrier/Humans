using Microsoft.EntityFrameworkCore;
using NodaTime;
using Humans.Application.DTOs;
using Humans.Application.Interfaces;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Infrastructure.Data;

namespace Humans.Infrastructure.Services;

/// <summary>
/// Service for managing contact fields with visibility controls.
/// </summary>
public class ContactFieldService : IContactFieldService
{
    private readonly HumansDbContext _dbContext;
    private readonly ITeamService _teamService;
    private readonly IClock _clock;

    // Request-scoped cache for viewer permissions to avoid N+1 queries during listing
    private bool? _cachedIsBoardMember;
    private bool? _cachedIsAnyLead;
    private HashSet<Guid>? _cachedViewerTeamIds;

    public ContactFieldService(
        HumansDbContext dbContext,
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
        var allowedVisibilities = GetAllowedVisibilities(accessLevel);

        var fields = await _dbContext.ContactFields
            .AsNoTracking()
            .Where(cf => cf.ProfileId == profileId && allowedVisibilities.Contains(cf.Visibility))
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

    /// <summary>
    /// Returns the set of visibility levels a viewer with the given access level can see.
    /// Visibility is stored as string in DB, so >= comparison doesn't work correctly.
    /// </summary>
    private static List<ContactFieldVisibility> GetAllowedVisibilities(ContactFieldVisibility accessLevel) =>
        accessLevel switch
        {
            ContactFieldVisibility.BoardOnly => [ContactFieldVisibility.BoardOnly, ContactFieldVisibility.LeadsAndBoard, ContactFieldVisibility.MyTeams, ContactFieldVisibility.AllActiveProfiles],
            ContactFieldVisibility.LeadsAndBoard => [ContactFieldVisibility.LeadsAndBoard, ContactFieldVisibility.MyTeams, ContactFieldVisibility.AllActiveProfiles],
            ContactFieldVisibility.MyTeams => [ContactFieldVisibility.MyTeams, ContactFieldVisibility.AllActiveProfiles],
            _ => [ContactFieldVisibility.AllActiveProfiles]
        };

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
        _cachedIsBoardMember ??= await _teamService.IsUserBoardMemberAsync(viewerUserId, cancellationToken);
        if (_cachedIsBoardMember.Value)
        {
            return ContactFieldVisibility.BoardOnly;
        }

        // Check if viewer is a lead of any team
        if (_cachedViewerTeamIds == null)
        {
            var viewerTeams = await _teamService.GetUserTeamsAsync(viewerUserId, cancellationToken);
            _cachedIsAnyLead = viewerTeams.Any(tm => tm.Role == TeamMemberRole.Lead);
            _cachedViewerTeamIds = viewerTeams
                .Where(tm => tm.Team.SystemTeamType != SystemTeamType.Volunteers)
                .Select(tm => tm.TeamId)
                .ToHashSet();
        }

        if (_cachedIsAnyLead!.Value)
        {
            return ContactFieldVisibility.LeadsAndBoard;
        }

        // Check if viewer shares any team with owner (excluding Volunteers since everyone is in it)
        var ownerTeams = await _teamService.GetUserTeamsAsync(ownerUserId, cancellationToken);
        var ownerTeamIds = ownerTeams
            .Where(tm => tm.Team.SystemTeamType != SystemTeamType.Volunteers)
            .Select(tm => tm.TeamId)
            .ToHashSet();

        var sharesTeam = _cachedViewerTeamIds.Intersect(ownerTeamIds).Any();
        if (sharesTeam)
        {
            return ContactFieldVisibility.MyTeams;
        }

        // Default: can only see AllActiveProfiles visibility
        return ContactFieldVisibility.AllActiveProfiles;
    }
}
