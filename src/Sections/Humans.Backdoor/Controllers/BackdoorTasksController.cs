using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;
using Humans.Backdoor.Filters;
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
[AllowAnonymous]
internal sealed class BackdoorTasksController(
    ITasksWrite tasks,
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
        var denied = await TeamsWithoutManageAsync(model, actorUserId, ct);
        if (denied.Count > 0)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = "Tasks.Manage is needed on every team in the payload.", teams = denied });

        var result = await tasks.BulkLoadAsync(model.ToRequest(), actorUserId, ct);
        if (!result.Succeeded)
            return UnprocessableEntity(new { problems = result.Problems });

        logger.LogInformation(
            "Bulk task load by {ActorUserId}: {Count} task(s), dry run {DryRun}", actorUserId, result.Tasks.Count, result.DryRun);
        return Ok(new { dryRun = result.DryRun, created = result.Created, tasks = result.Tasks });
    }

    /// <summary>
    /// The team references the owner may not manage. A reference that matches no team is answered the same way, so a
    /// key never learns whether a team it cannot manage exists, hidden teams included.
    /// </summary>
    private async Task<List<string>> TeamsWithoutManageAsync(ApiBulkTasksModel model, Guid actorUserId, CancellationToken ct)
    {
        var denied = new List<string>();
        foreach (var reference in model.Tasks.Select(t => t.Team?.Trim()).Where(r => !string.IsNullOrEmpty(r)).Distinct(StringComparer.Ordinal))
        {
            var team = await teams.GetTeamBySlugAsync(reference!, ct)
                ?? (Guid.TryParse(reference, out var id) ? await teams.GetTeamAsync(id, ct) : null);
            if (team is null || !await permissions.HasAsync(actorUserId, TasksPermissions.Manage, new(TeamOwner.Kind, team.Id), ct))
                denied.Add(reference!);
        }

        return denied;
    }
}

internal sealed class ApiBulkTasksModel
{
    public List<ApiBulkTaskModel> Tasks { get; set; } = [];

    public bool DryRun { get; set; }

    public BulkLoadRequest ToRequest() => new([.. Tasks.Select(t => t.ToTask())], DryRun);
}

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

    [EnumDataType(typeof(SlotPriority), ErrorMessage = "Validation_InvalidValue")]
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public SlotPriority Priority { get; set; } = SlotPriority.Important;

    public int SlotCount { get; set; } = 1;

    public bool NeedsApproval { get; set; }

    public string? ApprovalReason { get; set; }

    public bool IsHidden { get; set; }

    /// <summary>Keys of other tasks in the payload.</summary>
    public List<string>? DependsOn { get; set; }

    public BulkLoadTask ToTask() => new(
        Key ?? string.Empty, Team ?? string.Empty, Title ?? string.Empty, Description, EventId, Start?.ToDate(), Due?.ToDate(),
        Priority, SlotCount, NeedsApproval, ApprovalReason, IsHidden, DependsOn);
}

/// <summary>Exactly one of the three.</summary>
internal sealed class ApiBulkDateModel
{
    public DateOnly? Fixed { get; set; }

    public int? Day { get; set; }

    public DateOnly? RelativeDate { get; set; }

    public BulkLoadDate ToDate() => new(Convert(Fixed), Day, Convert(RelativeDate));

    private static LocalDate? Convert(DateOnly? date) =>
        date is { } d ? new LocalDate(d.Year, d.Month, d.Day) : null;
}
