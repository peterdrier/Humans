using Humans.AuditLog.Contracts;
using Humans.Base.Enums;
using Humans.Base.Interfaces;
using Humans.Shifts.Contracts;
using Humans.Shifts.Data;
using Humans.Shifts.Domain;
using Humans.Shifts.Services.Dtos;
using Humans.Teams.Contracts;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;

namespace Humans.Shifts.Services;

internal sealed class ShiftRotaManagement(
    IShiftManagementRepository repo,
    IAuditLogService auditLogService,
    IServiceProvider serviceProvider,
    EventCalendarResolver calendarResolver,
    ShiftEventSettings shiftSettings,
    CachingShiftViewService viewInvalidator,
    IClock clock) : IApplicationService
{
    // Lazy, to break the DI cycle with TeamService.
    private ITeamService TeamService => serviceProvider.GetRequiredService<ITeamService>();

    internal async Task<Guid> CreateRotaAsync(CreateRotaInput input, IReadOnlyList<Guid>? tagIds = null)
    {
        var rota = new Rota
        {
            Id = Guid.NewGuid(),
            TeamId = input.TeamId,
            EventSettingsId = input.EventSettingsId,
            Name = input.Name,
            Priority = input.Priority,
            Policy = input.Policy,
            Period = input.Period,
            IsVisibleToVolunteers = input.IsVisibleToVolunteers,
            CreatedAt = clock.GetCurrentInstant(),
        };
        await CreateRotaAsync(rota, tagIds);
        return rota.Id;
    }

    internal async Task CreateRotaAsync(Rota rota, IReadOnlyList<Guid>? tagIds = null)
    {
        var team = await TeamService.GetTeamAsync(rota.TeamId);

        if (team is null)
            throw new InvalidOperationException("Team not found.");
        if (team.SystemTeamType != SystemTeamType.None)
            throw new InvalidOperationException("Rotas cannot be created on system teams.");

        // Any real Settings cycle will do; it need not be the active one.
        if (await calendarResolver.GetAsync(rota.EventSettingsId) is null)
            throw new InvalidOperationException("Event not found.");

        await shiftSettings.EnsureKnobsRowAsync(rota.EventSettingsId);

        rota.UpdatedAt = clock.GetCurrentInstant();
        try
        {
            await repo.SaveRotaAsync(rota, EntityMutationMode.Add,
                tagIds: tagIds is { Count: > 0 } ? tagIds : null);
        }
        finally
        {
            viewInvalidator.InvalidateRota(rota.Id);
        }
    }

    internal async Task<bool> UpdateRotaAsync(UpdateRotaInput input)
    {
        var rota = await repo.GetRotaAsync(input.RotaId, RotaReadShape.None);
        if (rota is null) return false;

        rota.Name = input.Name;
        rota.Description = input.Description;
        rota.PracticalInfo = input.PracticalInfo;
        rota.Priority = input.Priority;
        rota.Policy = input.Policy;
        rota.Period = input.Period;
        rota.IsVisibleToVolunteers = input.IsVisibleToVolunteers;
        rota.UpdatedAt = clock.GetCurrentInstant();
        try
        {
            await repo.SaveRotaAsync(rota, EntityMutationMode.Update, tagIds: input.TagIds);
        }
        finally
        {
            viewInvalidator.InvalidateRota(rota.Id);
        }

        return true;
    }

    internal async Task<RotaMoveResult> MoveRotaToTeamAsync(MoveRotaInput input)
    {
        var rota = await repo.GetRotaAsync(input.RotaId, RotaReadShape.None);
        if (rota is null || rota.TeamId != input.SourceTeamId)
            return RotaMoveResult.Failure("Rota not found.");

        var targetTeam = await TeamService.GetTeamAsync(input.TargetTeamId);
        if (targetTeam is null)
            return RotaMoveResult.Failure("Target team not found.");
        if (targetTeam.ParentTeamId is not null)
            return RotaMoveResult.Failure("Rotas can only be moved to parent teams (departments).");
        if (targetTeam.SystemTeamType != SystemTeamType.None)
            return RotaMoveResult.Failure("Rotas cannot be moved to system teams.");
        if (rota.TeamId == input.TargetTeamId)
            return RotaMoveResult.Failure("Rota is already in this team.");

        var oldTeam = await TeamService.GetTeamAsync(rota.TeamId);
        var oldTeamName = oldTeam?.Name ?? "(unknown)";

        // Targeted write: only TeamId + UpdatedAt are marked modified so concurrent edits don't clobber.
        try
        {
            await repo.UpdateRotaTeamAssignmentAsync(
                rota.Id, input.TargetTeamId, clock.GetCurrentInstant());
        }
        finally
        {
            viewInvalidator.InvalidateRota(rota.Id);
        }

        await auditLogService.LogAsync(
            ShiftsAuditActions.RotaMovedToTeam, nameof(Rota), rota.Id,
            $"Moved rota '{rota.Name}' from '{oldTeamName}' to '{targetTeam.Name}'",
            input.ActorUserId,
            // A persisted audit discriminator matched by exact equality, so a literal: Base cannot name the entity.
            relatedEntityId: input.TargetTeamId, relatedEntityType: "Team");

        return RotaMoveResult.Success($"Rota '{rota.Name}' moved to {targetTeam.Name}.", targetTeam.Slug);
    }
}
