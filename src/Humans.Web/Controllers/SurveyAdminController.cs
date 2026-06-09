using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Humans.Application.Interfaces.Surveys;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Humans.Domain.Enums;
using Humans.Web.Authorization;
using Humans.Web.Extensions;
using Humans.Web.Models;
using Humans.Web.Models.Survey;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;

namespace Humans.Web.Controllers;

/// <summary>
/// Board/Admin survey authoring: index, builder (create/edit), open/close. Controllers parse → call
/// the service → format; sorting and VM↔DTO mapping live here (hard rule). Send lives in Phase 3.
/// </summary>
[Authorize(Policy = PolicyNames.BoardOrAdmin)]
[Route("Survey/Admin")]
public class SurveyAdminController(
    ISurveyService surveyService,
    ITeamServiceRead teamService,
    IUserServiceRead userService,
    ILogger<SurveyAdminController> logger) : HumansControllerBase(userService)
{
    private static readonly DateTimeZone Zone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    private static readonly JsonSerializerOptions ExportJsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var summaries = await surveyService.GetSummariesAsync(ct);
        var ordered = summaries
            .OrderBy(s => s.Status)
            .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return View(new SurveyAdminIndexViewModel { Surveys = ordered });
    }

    [HttpGet("Create")]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        var vm = new SurveyBuilderViewModel { Teams = await LoadTeamsAsync(ct) };
        return View("Builder", vm);
    }

    [HttpGet("Edit/{id:guid}")]
    public async Task<IActionResult> Edit(Guid id, CancellationToken ct)
    {
        var detail = await surveyService.GetForEditAsync(id, ct);
        if (detail is null) return NotFound();

        var vm = SurveyBuilderViewModel.FromDetail(detail, await LoadTeamsAsync(ct), Zone);
        return View("Builder", vm);
    }

    [HttpPost("Save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(SurveyBuilderViewModel model, string? submitAction, CancellationToken ct)
    {
        var actorId = GetCurrentUserId();
        if (actorId is null) return Forbid();

        if (!ModelState.IsValid)
        {
            model.Teams = await LoadTeamsAsync(ct);
            return View("Builder", model);
        }

        Guid id;
        try
        {
            var input = model.ToEditInput(Zone);
            if (model.Id is null)
            {
                id = await surveyService.CreateAsync(input, actorId.Value, ct);
            }
            else
            {
                id = model.Id.Value;
                await surveyService.UpdateAsync(id, input, actorId.Value, ct);
            }
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Survey save rejected for {SurveyId}: {Reason}", model.Id, ex.Message);
            ModelState.AddModelError(string.Empty, ex.Message);
            model.Teams = await LoadTeamsAsync(ct);
            return View("Builder", model);
        }

        // The save is committed at this point — a translation failure must not re-render the
        // builder as unsaved (a re-submit would double-create), so it reports and redirects.
        if (string.Equals(submitAction, "save-translate", StringComparison.Ordinal))
        {
            try
            {
                var filled = await surveyService.PreFillTranslationsAsync(
                    id, CultureCatalog.SupportedCultureCodes, actorId.Value, ct);
                SetSuccess(filled > 0
                    ? $"Survey saved; {filled} missing translation(s) pre-filled — review them before opening."
                    : "Survey saved — no missing translations to fill.");
            }
            catch (InvalidOperationException ex)
            {
                logger.LogWarning("Survey translation failed for {SurveyId}: {Reason}", id, ex.Message);
                SetError($"Survey saved, but translation failed: {ex.Message}");
            }
        }
        else
        {
            SetSuccess(model.Id is null ? "Survey created." : "Survey saved.");
        }

        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost("Open/{id:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Open(Guid id, CancellationToken ct)
    {
        var actorId = GetCurrentUserId();
        if (actorId is null) return Forbid();
        await RunStatusTransitionAsync(id, () => surveyService.OpenAsync(id, actorId.Value, ct), "Survey opened.");
        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost("Close/{id:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Close(Guid id, CancellationToken ct)
    {
        var actorId = GetCurrentUserId();
        if (actorId is null) return Forbid();
        await RunStatusTransitionAsync(id, () => surveyService.CloseAsync(id, actorId.Value, ct), "Survey closed.");
        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpGet("Send/{id:guid}")]
    public async Task<IActionResult> Send(Guid id, CancellationToken ct)
    {
        var detail = await surveyService.GetForEditAsync(id, ct);
        if (detail is null) return NotFound();

        var previewCount = await surveyService.PreviewAudienceCountAsync(id, ct);
        var statuses = await surveyService.GetInviteStatusesAsync(id, ct);

        var vm = new SurveySendViewModel
        {
            Id = detail.Id,
            Title = detail.Editable.Title.Resolve(detail.Editable.DefaultCulture, detail.Editable.DefaultCulture),
            Status = detail.Status,
            AudienceType = detail.Editable.AudienceType,
            PreviewCount = previewCount,
            Invitations = statuses.OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase).ToList(),
        };
        return View(vm);
    }

    [HttpPost("Send/{id:guid}")]
    [ActionName(nameof(Send))]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendInvites(Guid id, CancellationToken ct)
    {
        var actorId = GetCurrentUserId();
        if (actorId is null) return Forbid();

        try
        {
            var result = await surveyService.SendInvitesAsync(id, actorId.Value, ct);
            SetSuccess($"Sent {result.InvitationsCreated} new invitation(s); {result.EmailsQueued} email(s) queued, {result.Failed} failed.");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Send invites rejected for survey {SurveyId}: {Reason}", id, ex.Message);
            SetError(ex.Message);
        }
        return RedirectToAction(nameof(Send), new { id });
    }

    [HttpGet("Results/{id:guid}")]
    public async Task<IActionResult> Results(Guid id, CancellationToken ct)
    {
        var results = await surveyService.GetResultsAsync(id, ct);
        if (results is null) return NotFound();

        return View(SurveyResultsBuilder.Build(results));
    }

    [HttpGet("Results/{id:guid}/Export.csv")]
    public async Task<IActionResult> ExportCsv(Guid id, CancellationToken ct)
    {
        var export = await surveyService.GetResponseExportAsync(id, ct);
        if (export is null) return NotFound();

        var bytes = SurveyCsvExportBuilder.Build(export);
        return File(bytes, "text/csv", $"survey-{id}.csv");
    }

    [HttpGet("Results/{id:guid}/Export.json")]
    public async Task<IActionResult> ExportJson(Guid id, CancellationToken ct)
    {
        var export = await surveyService.GetResponseExportAsync(id, ct);
        if (export is null) return NotFound();

        var json = JsonSerializer.Serialize(export, ExportJsonOptions);
        return File(Encoding.UTF8.GetBytes(json), "application/json", $"survey-{id}.json");
    }

    private async Task RunStatusTransitionAsync(Guid id, Func<Task> transition, string success)
    {
        try
        {
            await transition();
            SetSuccess(success);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Status transition rejected for survey {SurveyId}: {Reason}", id, ex.Message);
            SetError(ex.Message);
        }
    }

    private async Task<IReadOnlyList<SurveyTeamOption>> LoadTeamsAsync(CancellationToken ct)
    {
        var teams = await teamService.GetTeamsAsync(ct);
        return teams.Values
            .Where(t => t.IsActive)
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => new SurveyTeamOption(t.Id, t.Name))
            .ToList();
    }
}
