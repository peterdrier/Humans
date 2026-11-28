using JetBrains.Annotations;
using System.Text.Json.Serialization;
using Humans.Backdoor.Filters;
using Humans.Backdoor.Services;
using Humans.Base.Controllers;
using Humans.Permissions.Contracts;
using Humans.Tasks.Contracts;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Backdoor.Controllers;

/// <summary>
/// Loads a whole set of tasks at once. Tasks holds every rule; this checks only that the key's owner holds
/// <c>Tasks.Manage</c> on each team the payload names, before Tasks sees it.
/// </summary>
[ApiController]
[Route("api/backdoor/tasks")]
[ServiceFilter(typeof(BackdoorApiKeyAuthFilter))]
[BackdoorScope(BackdoorScope.Tasks)]
[AllowAnonymous]
internal sealed class BackdoorTasksController(
    ITaskWrite tasks,
    IPermissionService permissions,
    ITeamService teams,
    IUserService users,
    ILogger<BackdoorTasksController> logger)
    : ApiControllerBase(users)
{
    /// <summary>
    /// 403 naming the teams the key's owner may not manage; 422 with every problem found; otherwise 200 with the keys
    /// and, unless it was a dry run, the new ids.
    /// </summary>
    [HttpPost("bulk")]
    public async Task<IActionResult> Bulk([FromBody] ApiBulkTasksModel model)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);
        if (model.Tasks.Any(t => t is null))
            return BadRequest(new { error = "Every entry in tasks must be an object." });
        if (GetCurrentUserId() is not { } actorUserId)
            return Unauthorized();

        var ct = HttpContext.RequestAborted;
        var (teamIds, denied, unknown) = await ResolveManagedTeamsAsync(model, actorUserId, ct);
        if (denied.Count > 0)
        {
            logger.LogWarning("Bulk task load by {ActorUserId} refused: no such team {UnknownTeams}; no Tasks.Manage on {UnmanagedTeams}",
                actorUserId, unknown, denied.Except(unknown, StringComparer.Ordinal).ToList());
            return StatusCode(StatusCodes.Status403Forbidden,
                new { error = "No such team, or no Tasks.Manage on it.", teams = denied });
        }

        var result = await tasks.BulkLoadAsync(model.ToRequest(teamIds), actorUserId, ct);
        if (!result.Succeeded)
            return UnprocessableEntity(new { problems = result.Problems });

        logger.LogInformation(
            "Bulk task load by {ActorUserId}: {Count} task(s), dry run {DryRun}", actorUserId, result.Tasks.Count, result.DryRun);
        return Ok(new { dryRun = result.DryRun, created = result.Created, tasks = result.Tasks });
    }

    /// <summary>
    /// Each team reference's id, the references the owner may not manage in input order, and which of those match no
    /// team. The two refusals are logged apart but answered the same way, in the same order, so a key never learns
    /// whether a team it cannot manage exists, hidden teams included.
    /// </summary>
    private async Task<(Dictionary<string, Guid> TeamIds, List<string> Denied, List<string> Unknown)> ResolveManagedTeamsAsync(
        ApiBulkTasksModel model, Guid actorUserId, CancellationToken ct)
    {
        var teamIds = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var denied = new List<string>();
        var unknown = new List<string>();
        foreach (var reference in model.Tasks.Select(t => t.Team?.Trim()).Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.Ordinal))
        {
            var team = await teams.GetTeamBySlugAsync(reference!, ct)
                ?? (Guid.TryParse(reference, out var id) ? await teams.GetTeamAsync(id, ct) : null);
            if (team is null || !await permissions.HasAsync(actorUserId, TasksPermissions.Manage, new(TeamOwner.Kind, team.Id), ct))
            {
                denied.Add(reference!);
                if (team is null)
                    unknown.Add(reference!);
            }
            else
                teamIds[reference!] = team.Id;
        }

        return (teamIds, denied, unknown);
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class ApiBulkTasksModel
{
    public List<ApiBulkTaskModel> Tasks { get; set; } = [];

    public bool DryRun { get; set; }

    /// <summary>A task whose team reference is not in <paramref name="teamIds"/> goes to Tasks with no team, which refuses it.</summary>
    /// <param name="teamIds">Each team reference's id.</param>
    public BulkLoadRequest ToRequest(IReadOnlyDictionary<string, Guid> teamIds) => new([.. Tasks.Select(t => t.ToTask(teamIds))], DryRun);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
internal sealed class ApiBulkTaskModel
{
    /// <summary>Means something only inside the payload.</summary>
    public string? Key { get; set; }

    /// <summary>The team's slug, custom slug or id.</summary>
    public string? Team { get; set; }

    public string? Title { get; set; }

    public string? Description { get; set; }

    public Guid? EventId { get; set; }

    public ApiBulkDateModel? Start { get; set; }

    public ApiBulkDateModel? Due { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SlotPriority Priority { get; set; } = SlotPriority.Important;

    public int SlotCount { get; set; } = 1;

    public bool NeedsApproval { get; set; }

    public string? ApprovalReason { get; set; }

    public bool IsHidden { get; set; }

    /// <summary>Keys of other tasks in the payload.</summary>
    public List<string>? DependsOn { get; set; }

    public BulkLoadTask ToTask(IReadOnlyDictionary<string, Guid> teamIds) => new(
        Key ?? string.Empty, Team?.Trim() is { } team && teamIds.TryGetValue(team, out var teamId) ? teamId : Guid.Empty, Title ?? string.Empty, Description, EventId, Start?.ToDate(), Due?.ToDate(),
        Priority, SlotCount, NeedsApproval, ApprovalReason, IsHidden, DependsOn);
}

/// <summary>Exactly one of the three.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
internal sealed class ApiBulkDateModel
{
    public DateOnly? Fixed { get; set; }

    public int? Day { get; set; }

    public DateOnly? RelativeDate { get; set; }

    public BulkLoadDate ToDate() => new(Convert(Fixed), Day, Convert(RelativeDate));

    private static LocalDate? Convert(DateOnly? date) =>
        date is { } d ? new LocalDate(d.Year, d.Month, d.Day) : null;
}
