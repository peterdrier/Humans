using NodaTime;
using Microsoft.Extensions.Logging;
using Humans.Application.DTOs;
using Humans.Application.Interfaces.Repositories;
using Humans.Domain.Entities;
using Humans.Domain.Enums;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Auth;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Users;

namespace Humans.Application.Services.Profiles;

/// <summary>
/// Service for managing contact fields with visibility controls.
/// </summary>
public sealed class ContactFieldService : IContactFieldService, IUserMerge
{
    private readonly IContactFieldRepository _repository;
    private readonly IProfileRepository _profileRepository;
    private readonly IUserService _userService;
    private readonly ITeamService _teamService;
    private readonly IRoleAssignmentService _roleAssignmentService;
    private readonly IUserInfoInvalidator _userInfoInvalidator;
    private readonly IClock _clock;
    private readonly ILogger<ContactFieldService> _logger;

    // Request-scoped cache for viewer permissions (avoid N+1 in listing).
    private bool? _cachedIsBoardMember;
    private bool? _cachedIsAnyCoordinator;
    private HashSet<Guid>? _cachedViewerTeamIds;

    public ContactFieldService(
        IContactFieldRepository repository,
        IProfileRepository profileRepository,
        IUserService userService,
        ITeamService teamService,
        IRoleAssignmentService roleAssignmentService,
        IUserInfoInvalidator userInfoInvalidator,
        IClock clock,
        ILogger<ContactFieldService> logger)
    {
        _repository = repository;
        _profileRepository = profileRepository;
        _userService = userService;
        _teamService = teamService;
        _roleAssignmentService = roleAssignmentService;
        _userInfoInvalidator = userInfoInvalidator;
        _clock = clock;
        _logger = logger;
    }

    public async Task<IReadOnlyList<ContactFieldDto>> GetVisibleContactFieldsAsync(
        Guid userId,
        Guid viewerUserId,
        CancellationToken cancellationToken = default)
    {
        var info = await _userService.GetUserInfoAsync(userId, cancellationToken);
        if (info?.Profile is null)
            return [];

        var accessLevel = await GetViewerAccessLevelAsync(
            userId, viewerUserId, cancellationToken);
        var allowedVisibilities = GetAllowedVisibilities(accessLevel);

        return info.Profile.ContactFields
            .Where(cf => allowedVisibilities.Contains(cf.Visibility))
            .Select(cf => new ContactFieldDto(
                cf.Id,
                cf.FieldType,
                cf.FieldType == ContactFieldType.Other ? cf.CustomLabel ?? "Other" : cf.FieldType.ToString(),
                cf.Value,
                cf.Visibility))
            .ToList();
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

        // Entities detached after call (IDbContextFactory) — pass mutations explicitly to BatchSaveAsync.
        var existingFields = await _repository.GetByProfileIdForMutationAsync(profileId, cancellationToken);

        var existingById = existingFields.ToDictionary(cf => cf.Id);
        var incomingIds = fields.Where(f => f.Id.HasValue).Select(f => f.Id!.Value).ToHashSet();

        var toDelete = existingFields.Where(cf => !incomingIds.Contains(cf.Id)).ToList();

        var toAdd = new List<ContactField>();
        var toUpdate = new List<ContactField>();

        foreach (var dto in fields)
        {
            if (dto.Id.HasValue && existingById.TryGetValue(dto.Id.Value, out var existing))
            {
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

        // see #703 — UserInfo invalidation bypasses interceptor + caching decorator; failure self-heals on next miss.
        var ownerUserId = await _profileRepository.GetOwnerUserIdAsync(profileId, cancellationToken);
        if (ownerUserId is not null)
        {
            try
            {
                await _userInfoInvalidator.InvalidateAsync(ownerUserId.Value, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(
                    "ContactFieldService: UserInfo invalidation failed for {UserId}: {ExType}",
                    ownerUserId.Value, ex.GetType().Name);
            }
        }
    }

    public async Task<ContactFieldVisibility> GetViewerAccessLevelAsync(
        Guid ownerUserId,
        Guid viewerUserId,
        CancellationToken cancellationToken = default)
    {
        if (ownerUserId == viewerUserId)
            return ContactFieldVisibility.BoardOnly;

        _cachedIsBoardMember ??= await _roleAssignmentService.IsUserBoardMemberAsync(viewerUserId, cancellationToken);
        if (_cachedIsBoardMember.Value)
            return ContactFieldVisibility.BoardOnly;

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

        // Shared team excluding Volunteers.
        var ownerTeams = await _teamService.GetUserTeamsAsync(ownerUserId, cancellationToken);
        var ownerTeamIds = ownerTeams
            .Where(tm => tm.Team.SystemTeamType != SystemTeamType.Volunteers)
            .Select(tm => tm.TeamId)
            .ToHashSet();

        if (_cachedViewerTeamIds.Intersect(ownerTeamIds).Any())
            return ContactFieldVisibility.MyTeams;

        return ContactFieldVisibility.AllActiveProfiles;
    }

    public Task ReassignAsync(Guid sourceUserId, Guid targetUserId, Guid actorUserId, Instant updatedAt,
        CancellationToken cancellationToken)
        => _repository.ReassignToUserAsync(sourceUserId, targetUserId, updatedAt, cancellationToken);

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
