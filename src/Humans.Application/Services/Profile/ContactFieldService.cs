using NodaTime;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Profiles;

namespace Humans.Application.Services.Profile;

/// <summary>
/// Service for managing contact fields with visibility controls.
/// </summary>
public sealed class ContactFieldService : IContactFieldService
{
    private readonly IContactFieldRepository _repository;
    private readonly IProfileRepository _profileRepository;
    private readonly ITeamService _teamService;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly IClock _clock;

    // Request-scoped cache for viewer permissions to avoid N+1 queries during listing
    private bool? _cachedIsBoardMember;
    private bool? _cachedIsAnyCoordinator;
    private HashSet<Guid>? _cachedViewerTeamIds;

    public ContactFieldService(
        IContactFieldRepository repository,
        IProfileRepository profileRepository,
        ITeamService teamService,
        IRoleAssignmentService roleAssignmentService,
        IClock clock)
    {
        _repository = repository;
        _profileRepository = profileRepository;
        _teamService = teamService;
        _roleAssignmentService = roleAssignmentService;
        _clock = clock;
    }

    public async Task<IReadOnlyList<ContactFieldDto>> GetVisibleContactFieldsAsync(
        Guid profileId,
        Guid viewerUserId,
        CancellationToken cancellationToken = default)
    {
        // Resolve profileId → ownerUserId via a scalar repo query
        var ownerUserId = await _profileRepository.GetOwnerUserIdAsync(profileId, cancellationToken);
        if (ownerUserId is null)
            return [];

        var accessLevel = await GetViewerAccessLevelAsync(
            ownerUserId.Value, viewerUserId, cancellationToken);
        var allowedVisibilities = GetAllowedVisibilities(accessLevel);

        var fields = await _repository.GetVisibleByProfileIdAsync(
            profileId, allowedVisibilities, cancellationToken);

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
        var fields = await _repository.GetByProfileIdReadOnlyAsync(profileId, cancellationToken);

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

        // Load existing fields. With IDbContextFactory the repo context is
        // short-lived, so entities are detached after the call — mutations must
        // be passed explicitly to BatchSaveAsync rather than relying on tracking.
        var existingFields = await _repository.GetByProfileIdForMutationAsync(profileId, cancellationToken);

        var existingById = existingFields.ToDictionary(cf => cf.Id);
        var incomingIds = fields.Where(f => f.Id.HasValue).Select(f => f.Id!.Value).ToHashSet();

        // Delete fields no longer present
        var toDelete = existingFields.Where(cf => !incomingIds.Contains(cf.Id)).ToList();

        // Add new fields; collect mutated existing fields as toUpdate
        var toAdd = new List<ContactField>();
        var toUpdate = new List<ContactField>();

        foreach (var dto in fields)
        {
            if (dto.Id.HasValue && existingById.TryGetValue(dto.Id.Value, out var existing))
            {
                // Mutate the detached entity and add to toUpdate
                existing.FieldType = dto.FieldType;
                existing.CustomLabel = dto.CustomLabel;
                existing.Value = dto.Value;
                existing.Visibility = dto.Visibility;
                existing.DisplayOrder = dto.DisplayOrder;
                existing.UpdatedAt = now;
                toUpdate.Add(existing);
            }
            else
            {
                toAdd.Add(new ContactField
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
                });
            }
        }

        await _repository.BatchSaveAsync(toAdd, toUpdate, toDelete, cancellationToken);
    }

    public async Task<ContactFieldVisibility> GetViewerAccessLevelAsync(
        Guid ownerUserId,
        Guid viewerUserId,
        CancellationToken cancellationToken = default)
    {
        // Self viewing - can see everything
        if (ownerUserId == viewerUserId)
            return ContactFieldVisibility.BoardOnly;

        // Board member - can see everything
        _cachedIsBoardMember ??= await _roleAssignmentService.IsUserBoardMemberAsync(viewerUserId, cancellationToken);
        if (_cachedIsBoardMember.Value)
            return ContactFieldVisibility.BoardOnly;

        // Check if viewer is a coordinator of any team
        if (_cachedViewerTeamIds is null)
        {
            var viewerTeams = await _teamService.GetUserTeamsAsync(viewerUserId, cancellationToken);
            _cachedIsAnyCoordinator = viewerTeams.Any(tm => tm.Role == TeamMemberRole.Coordinator);
            _cachedViewerTeamIds = viewerTeams
                .Where(tm => tm.Team.SystemTeamType != SystemTeamType.Volunteers)
                .Select(tm => tm.TeamId)
                .ToHashSet();
        }

        if (_cachedIsAnyCoordinator!.Value)
            return ContactFieldVisibility.CoordinatorsAndBoard;

        // Check if viewer shares any team with owner (excluding Volunteers)
        var ownerTeams = await _teamService.GetUserTeamsAsync(ownerUserId, cancellationToken);
        var ownerTeamIds = ownerTeams
            .Where(tm => tm.Team.SystemTeamType != SystemTeamType.Volunteers)
            .Select(tm => tm.TeamId)
            .ToHashSet();

        if (_cachedViewerTeamIds.Intersect(ownerTeamIds).Any())
            return ContactFieldVisibility.MyTeams;

        return ContactFieldVisibility.AllActiveProfiles;
    }

    private static List<ContactFieldVisibility> GetAllowedVisibilities(ContactFieldVisibility accessLevel) =>
        accessLevel switch
        {
            ContactFieldVisibility.BoardOnly =>
            [
                ContactFieldVisibility.BoardOnly,
                ContactFieldVisibility.CoordinatorsAndBoard,
                ContactFieldVisibility.MyTeams,
                ContactFieldVisibility.AllActiveProfiles
            ],
            ContactFieldVisibility.CoordinatorsAndBoard =>
            [
                ContactFieldVisibility.CoordinatorsAndBoard,
                ContactFieldVisibility.MyTeams,
                ContactFieldVisibility.AllActiveProfiles
            ],
            ContactFieldVisibility.MyTeams =>
            [
                ContactFieldVisibility.MyTeams,
                ContactFieldVisibility.AllActiveProfiles
            ],
            _ => [ContactFieldVisibility.AllActiveProfiles]
        };
}
