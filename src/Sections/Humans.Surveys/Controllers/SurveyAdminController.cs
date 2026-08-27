using Humans.Surveys.Contracts;
using Humans.Base.Controllers;
using Humans.Surveys.Services;
using Humans.Teams.Contracts;
using Humans.Surveys.Domain;
using Humans.Base.Authorization;
using Humans.Base.Extensions;
using Humans.Surveys.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NodaTime;
using Humans.Users.Contracts;

namespace Humans.Surveys.Controllers;

/// <summary>
/// Board/Admin survey authoring: index, builder (create/edit), open/close, preview, send, results
/// and CSV/JSON export. Controllers parse → call the service → format; sorting and VM↔DTO mapping
/// live here (hard rule).
/// </summary>
[Authorize(Policy = PolicyNames.BoardOrAdmin)]
[Route("Survey/Admin")]
internal sealed class SurveyAdminController(
    ISurveyService surveyService,
    ITeamServiceRead teamService,
    IUserServiceRead userService,
    ILogger<SurveyAdminController> logger) : HumansControllerBase(userService)
{
    private static readonly DateTimeZone Zone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

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

    [HttpGet("Preview/{id:guid}")]
    public async Task<IActionResult> Preview(Guid id, string? culture, CancellationToken ct)
    {
        var detail = await surveyService.GetForEditAsync(id, ct);
        if (detail is null) return NotFound();

        var editable = detail.Editable;
        var resolvedCulture = SurveyPageViewModelFactory.ResolveCulture(culture, editable.DefaultCulture);
        var vm = new SurveyIntroViewModel
        {
            Title = editable.Title.Resolve(resolvedCulture, editable.DefaultCulture),
            Intro = editable.Intro.Resolve(resolvedCulture, editable.DefaultCulture),
            Culture = resolvedCulture,
            AllowAnonymous = editable.AllowAnonymous,
            ShowAnonymitySelector = editable.AllowAnonymous,
            IsPreview = true,
            PreviewSurveyId = detail.Id,
        };
        return View("~/Views/Survey/Intro.cshtml", vm);
    }

    [HttpGet("Preview/{id:guid}/Page")]
    public async Task<IActionResult> PreviewPage(Guid id, string? culture, int? page, CancellationToken ct)
    {
        var detail = await surveyService.GetForEditAsync(id, ct);
        if (detail is null) return NotFound();

        var editable = detail.Editable;
        var pages = SurveyWizardFlow.OrderedPages(editable.Questions);
        if (pages.Count == 0)
            return RedirectToAction(nameof(PreviewThankYou), new { id, culture });

        var selectedPage = page is not null && pages.Contains(page.Value) ? page.Value : pages[0];
        var resolvedCulture = SurveyPageViewModelFactory.ResolveCulture(culture, editable.DefaultCulture);
        var state = new SurveyWizardState
        {
            SurveyId = id,
            Culture = resolvedCulture,
            CurrentPage = selectedPage,
        };
        var questions = editable.Questions
            .Where(question => question.PageNumber == selectedPage)
            .OrderBy(question => question.Order)
            .ToList();
        var vm = SurveyPageViewModelFactory.Build(
            state, editable, questions, pages, isPublic: false, routeKey: string.Empty,
            isPreview: true, previewSurveyId: id);
        return View("~/Views/Survey/Page.cshtml", vm);
    }

    [HttpGet("Preview/{id:guid}/ThankYou")]
    public async Task<IActionResult> PreviewThankYou(Guid id, string? culture, CancellationToken ct)
    {
        var detail = await surveyService.GetForEditAsync(id, ct);
        if (detail is null) return NotFound();

        var editable = detail.Editable;
        var resolvedCulture = SurveyPageViewModelFactory.ResolveCulture(culture, editable.DefaultCulture);
        var thankYou = editable.ThankYou.Resolve(resolvedCulture, editable.DefaultCulture);
        var vm = new SurveyThankYouViewModel
        {
            Title = editable.Title.Resolve(resolvedCulture, editable.DefaultCulture),
            ThankYou = thankYou,
            IsPreview = true,
            PreviewSurveyId = id,
        };
        return View("~/Views/Survey/ThankYou.cshtml", vm);
    }

    [HttpPost("Preview/{id:guid}/Email")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendPreviewEmail(
        Guid id,
        [FromServices] ISurveyPreviewEmailService previewEmailService,
        CancellationToken ct)
    {
        var actorId = GetCurrentUserId();
        if (actorId is null) return Forbid();

        try
        {
            var email = await previewEmailService.SendToUserAsync(id, actorId.Value, ct);
            SetSuccess($"Survey preview email queued for {email}.");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(
                "Survey preview email rejected for survey {SurveyId}, user {UserId}: {Reason}",
                id, actorId, ex.Message);
            SetError(ex.Message);
        }

        return RedirectToAction(nameof(Send), new { id });
    }

    [HttpGet("Preview/{id:guid}/Email")]
    public async Task<IActionResult> PreviewEmail(
        Guid id,
        [FromServices] ISurveyPreviewEmailService previewEmailService,
        CancellationToken ct)
    {
        var actorId = GetCurrentUserId();
        if (actorId is null) return Forbid();

        try
        {
            var message = await previewEmailService.PreviewForUserAsync(id, actorId.Value, ct);
            return View("EmailPreview", new SurveyEmailPreviewViewModel
            {
                SurveyId = id,
                Recipient = message.RecipientEmail,
                Subject = message.Subject,
                HtmlBody = message.HtmlBody,
            });
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(
                "Survey email preview rejected for survey {SurveyId}, user {UserId}: {Reason}",
                id, actorId, ex.Message);
            SetError(ex.Message);
            return RedirectToAction(nameof(Edit), new { id });
        }
    }

    [HttpPost("Save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Save(SurveyBuilderViewModel model, string? submitAction, CancellationToken ct)
    {
        var actorId = GetCurrentUserId();
        if (actorId is null) return Forbid();

        if (model.AudienceType == SurveyAudienceType.Team && model.AudienceTeamId is null)
        {
            ModelState.AddModelError(nameof(model.AudienceTeamId),
                "Choose a team for the Team audience.");
        }

        if (model.AudienceType == SurveyAudienceType.LoggedInSince && model.AudienceLoggedInSince is null)
        {
            ModelState.AddModelError(nameof(model.AudienceLoggedInSince),
                "A \"Logged in since\" cutoff date is required for the LoggedInSince audience.");
        }

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

        return string.Equals(submitAction, "save-review", StringComparison.Ordinal)
            ? RedirectToAction(nameof(Send), new { id })
            : RedirectToAction(nameof(Edit), new { id });
    }

    [HttpPost("Open/{id:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Open(Guid id, bool continueToSend, CancellationToken ct)
    {
        var actorId = GetCurrentUserId();
        if (actorId is null) return Forbid();
        await RunStatusTransitionAsync(id, () => surveyService.OpenAsync(id, actorId.Value, ct), "Survey opened.");
        return continueToSend
            ? RedirectToAction(nameof(Send), new { id })
            : RedirectToAction(nameof(Edit), new { id });
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

        var newRecipientCount = await surveyService.PreviewAudienceCountAsync(id, ct);
        var statuses = await surveyService.GetInviteStatusesAsync(id, ct);
        var audienceTeamName = detail.Editable.AudienceType == SurveyAudienceType.Team
            && detail.Editable.AudienceTeamId is { } teamId
                ? (await teamService.GetTeamAsync(teamId, ct))?.Name
                : null;

        var vm = new SurveySendViewModel
        {
            Id = detail.Id,
            Title = detail.Editable.Title.Resolve(detail.Editable.DefaultCulture, detail.Editable.DefaultCulture),
            Status = detail.Status,
            AudienceType = detail.Editable.AudienceType,
            AudienceTeamName = audienceTeamName,
            AudienceLoggedInSince = detail.Editable.AudienceLoggedInSince?.InZone(Zone).Date,
            NewRecipientCount = newRecipientCount,
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
    public async Task<IActionResult> Results(
        Guid id,
        CancellationToken ct,
        SurveyResultsScope scope = SurveyResultsScope.Combined)
    {
        var results = await surveyService.GetScopedResultsAsync(id, scope, ct);
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

        return File(SurveyJsonExportBuilder.Build(export), "application/json", $"survey-{id}.json");
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
