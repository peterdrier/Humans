using System.Globalization;
using Humans.Base.Controllers;
using Humans.Surveys.Authorization;
using Humans.Surveys.Services;
using Humans.Teams.Contracts;
using Humans.Surveys.Domain;
using Humans.Base.Authorization;
using Humans.Base.Extensions;
using Humans.Surveys.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NodaTime;
using Humans.Users.Contracts;

namespace Humans.Surveys.Controllers;

/// <summary>
/// Any Human with an approved profile may author their own Draft and submit it; Board/Admin get the rest. The author
/// reads their own survey's results once it closes. Ownership is enforced by <see cref="SurveyAuthorizationHandler"/>,
/// never by filtering in the view.
/// </summary>
[Authorize(Policy = PolicyNames.AppAccess)]
[Route("Survey/Admin")]
internal sealed class SurveyAdminController(
    ISurveyService surveyService,
    ITeamService teamService,
    IUserService userService,
    IAuthorizationService authorizationService,
    IStringLocalizer<SurveysResource> localizer,
    ILogger<SurveyAdminController> logger) : HumansControllerBase(userService)
{
    private static readonly DateTimeZone Zone = DateTimeZoneProviders.Tzdb["Europe/Madrid"];

    private SurveyViewer CurrentViewer(Guid actorId) => new(actorId, RoleChecks.IsAdminOrBoard(User));

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Forbid();

        var isBoardOrAdmin = RoleChecks.IsAdminOrBoard(User);
        var summaries = await surveyService.GetAdminSummariesAsync(
            new SurveyViewer(userId.Value, isBoardOrAdmin), ct);
        var ordered = summaries
            .OrderBy(s => s.Status)
            .ThenBy(s => s.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return View(new SurveyAdminIndexViewModel { Surveys = ordered, IsBoardOrAdmin = isBoardOrAdmin });
    }

    [HttpGet("Queue")]
    [Authorize(Policy = PolicyNames.BoardOrAdmin)]
    public async Task<IActionResult> Queue(CancellationToken ct)
    {
        var items = await surveyService.GetPendingApprovalQueueAsync(ct);
        return View(nameof(Queue), new SurveyPendingApprovalViewModel { Items = items });
    }

    [HttpPost("Approve/{id:guid}")]
    [Authorize(Policy = PolicyNames.BoardOrAdmin)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Approve(Guid id, CancellationToken ct)
    {
        var actorId = GetCurrentUserId();
        if (actorId is null) return Forbid();

        try
        {
            var viewer = new SurveyViewer(actorId.Value, RoleChecks.IsAdminOrBoard(User));
            var result = await surveyService.ApproveAndSendAsync(id, viewer, ct);
            SetSuccess(localizer["Surveys_Admin_Queue_Approved", result.EmailsQueued, result.Failed]);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Survey approval rejected for {SurveyId}: {Reason}", id, ex.Message);
            SetError(ex.Message);
        }
        return RedirectToAction(nameof(Queue));
    }

    [HttpPost("Reject/{id:guid}")]
    [Authorize(Policy = PolicyNames.BoardOrAdmin)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reject(Guid id, string note, CancellationToken ct)
    {
        var actorId = GetCurrentUserId();
        if (actorId is null) return Forbid();

        try
        {
            var viewer = new SurveyViewer(actorId.Value, RoleChecks.IsAdminOrBoard(User));
            await surveyService.RejectAsync(id, viewer, note, ct);
            SetSuccess(localizer["Surveys_Admin_Queue_Rejected"]);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Survey rejection rejected for {SurveyId}: {Reason}", id, ex.Message);
            ModelState.AddModelError(string.Empty, ex.Message);
            ViewData[$"RejectionNote:{id}"] = note;
            return await Queue(ct);
        }
        return RedirectToAction(nameof(Queue));
    }

    [HttpPost("Submit/{id:guid}")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Submit(Guid id, CancellationToken ct)
    {
        var actorId = GetCurrentUserId();
        if (actorId is null) return Forbid();

        var detail = await surveyService.GetForEditAsync(id, ct);
        if (detail is null) return NotFound();

        var auth = await authorizationService.AuthorizeAsync(User, detail, new SurveyOperationRequirement(SurveyOperation.Submit));
        if (!auth.Succeeded) return Forbid();

        try
        {
            var result = await surveyService.SubmitForApprovalAsync(id, actorId.Value, ct);
            if (result.Succeeded)
                SetSuccess(localizer["Surveys_Admin_Index_Submitted"]);
            else
                SetError(localizer[result.ErrorKey ?? "Surveys_SubmitFailed"]);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Survey submit-for-approval rejected for {SurveyId}: {Reason}", id, ex.Message);
            SetError(localizer["Surveys_SubmitFailed"]);
        }
        // Index, not Edit: a successful submit leaves the survey PendingApproval, which the
        // authorization handler no longer lets an ordinary author edit.
        return RedirectToAction(nameof(Index));
    }

    [HttpGet("Official/{id:guid}")]
    [Authorize(Policy = PolicyNames.BoardOrAdmin)]
    public async Task<IActionResult> Official(Guid id, CancellationToken ct)
    {
        var userId = GetCurrentUserId();
        if (userId is null) return Forbid();

        var link = await surveyService.GetOfficialLinkAsync(id, userId.Value, ct);
        if (link?.InvitationToken is { } token)
        {
            return RedirectToAction(nameof(SurveyController.Answer), "Survey", new { t = token });
        }
        if (link?.PublicSlug is { } slug)
        {
            return RedirectToAction(nameof(SurveyController.Public), "Survey", new { slug });
        }

        SetError("No active survey link is available for you.");
        return RedirectToAction(nameof(Send), new { id });
    }

    [HttpGet("Create")]
    public async Task<IActionResult> Create(CancellationToken ct)
    {
        var vm = new SurveyBuilderViewModel
        {
            Teams = await LoadTeamsAsync(ct),
            IsBoardOrAdmin = RoleChecks.IsAdminOrBoard(User)
        };
        return View("Builder", vm);
    }

    [HttpGet("Edit/{id:guid}")]
    public async Task<IActionResult> Edit(Guid id, CancellationToken ct)
    {
        var detail = await surveyService.GetForEditAsync(id, ct);
        if (detail is null) return NotFound();

        var auth = await authorizationService.AuthorizeAsync(User, detail, new SurveyOperationRequirement(SurveyOperation.Edit));
        if (!auth.Succeeded) return Forbid();

        var vm = SurveyBuilderViewModel.FromDetail(detail, await LoadTeamsAsync(ct), Zone);
        vm.HasSavedAnswers = await surveyService.HasSavedAnswersAsync(id, ct);
        vm.IsBoardOrAdmin = RoleChecks.IsAdminOrBoard(User);
        vm.CanSubmit = (await authorizationService.AuthorizeAsync(
            User, detail, new SurveyOperationRequirement(SurveyOperation.Submit))).Succeeded;
        return View("Builder", vm);
    }

    private async Task<(IActionResult? Error, SurveyDetail? Detail)> RequirePreviewAsync(Guid id, CancellationToken ct)
    {
        var detail = await surveyService.GetForEditAsync(id, ct);
        if (detail is null) return (NotFound(), null);
        var auth = await authorizationService.AuthorizeAsync(
            User, detail, new SurveyOperationRequirement(SurveyOperation.Preview));
        return auth.Succeeded ? (null, detail) : (Forbid(), null);
    }

    [HttpGet("Preview/{id:guid}")]
    public async Task<IActionResult> Preview(Guid id, string? culture, CancellationToken ct)
    {
        var access = await RequirePreviewAsync(id, ct);
        if (access.Error is not null) return access.Error;

        var detail = access.Detail!;
        var editable = detail.Editable;
        var resolvedCulture = SurveyPageViewModelFactory.ResolveCulture(culture, editable.DefaultCulture);
        var vm = new SurveyIntroViewModel
        {
            Title = editable.Title.Resolve(resolvedCulture, editable.DefaultCulture),
            Intro = editable.Intro.Resolve(resolvedCulture, editable.DefaultCulture),
            Culture = resolvedCulture,
            AllowAnonymous = editable.AllowAnonymous,
            ShowAnonymitySelector = editable.AllowAnonymous,
            IsAsociadoVote = editable.IsAsociadoVote,
            IsPreview = true,
            PreviewSurveyId = detail.Id,
        };
        return View("~/Views/Survey/Intro.cshtml", vm);
    }

    [HttpGet("Preview/{id:guid}/Page")]
    public async Task<IActionResult> PreviewPage(Guid id, string? culture, int? page, CancellationToken ct)
    {
        var access = await RequirePreviewAsync(id, ct);
        if (access.Error is not null) return access.Error;

        var detail = access.Detail!;
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
        var access = await RequirePreviewAsync(id, ct);
        if (access.Error is not null) return access.Error;

        var detail = access.Detail!;
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

        var access = await RequirePreviewAsync(id, ct);
        if (access.Error is not null) return access.Error;

        try
        {
            var result = await previewEmailService.SendToUserAsync(id, actorId.Value, ct);
            if (result.RecipientEmail is not null)
                SetSuccess(localizer["Surveys_PreviewEmailQueued", result.RecipientEmail]);
            else
                SetError(localizer[result.ErrorKey ?? "Surveys_PreviewQueueFailed"]);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(
                "Survey preview email rejected for survey {SurveyId}, user {UserId}: {Reason}",
                id, actorId, ex.Message);
            SetError(localizer["Surveys_PreviewQueueFailed"]);
        }

        // Send is Board-only, so an author who rehearsed their own invitation goes to the builder.
        return RedirectToAction(
            RoleChecks.IsAdminOrBoard(User) ? nameof(Send) : nameof(Edit), new { id });
    }

    [HttpGet("Preview/{id:guid}/Email")]
    public async Task<IActionResult> PreviewEmail(
        Guid id,
        [FromServices] ISurveyPreviewEmailService previewEmailService,
        CancellationToken ct)
    {
        var actorId = GetCurrentUserId();
        if (actorId is null) return Forbid();

        var access = await RequirePreviewAsync(id, ct);
        if (access.Error is not null) return access.Error;

        try
        {
            var result = await previewEmailService.PreviewForUserAsync(id, actorId.Value, ct);
            if (result.Preview is null)
            {
                SetError(localizer[result.ErrorKey ?? "Surveys_PreviewFailed"]);
                return RedirectToAction(nameof(Edit), new { id });
            }
            var message = result.Preview;
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
            SetError(localizer["Surveys_PreviewFailed"]);
            return RedirectToAction(nameof(Edit), new { id });
        }
    }

    [HttpPost("Save")]
    [ValidateAntiForgeryToken]
    [RequestSizeLimit(55 * 1024 * 1024)] // Five 10 MB Information images plus multipart overhead; service enforces each image cap.
    public async Task<IActionResult> Save(SurveyBuilderViewModel model, string? submitAction, CancellationToken ct)
    {
        var actorId = GetCurrentUserId();
        if (actorId is null) return Forbid();

        ValidateAudience(model);

        if (!ModelState.IsValid)
        {
            await PrepareBuilderForRenderAsync(model, ct);
            return View("Builder", model);
        }

        if (model.Id is { } existingId)
        {
            var existingDetail = await surveyService.GetForEditAsync(existingId, ct);
            if (existingDetail is null) return NotFound();

            var auth = await authorizationService.AuthorizeAsync(
                User, existingDetail, new SurveyOperationRequirement(SurveyOperation.Edit));
            if (!auth.Succeeded) return Forbid();
        }

        Guid id;
        try
        {
            var input = model.ToEditInput(Zone);
            var result = model.Id is null
                ? await surveyService.CreateAsync(input, actorId.Value, ct)
                : await surveyService.UpdateAsync(model.Id.Value, input, CurrentViewer(actorId.Value), ct);
            if (!result.Succeeded)
            {
                var error = result.Error;
                logger.LogWarning("Survey save rejected for {SurveyId}: {Reason}", model.Id, error?.DiagnosticMessage);
                ModelState.AddModelError(string.Empty, error?.Key is { } key
                    ? localizer[key, error.Args].Value
                    : localizer["Surveys_Validation_SaveFailed"].Value);
                await PrepareBuilderForRenderAsync(model, ct);
                return View("Builder", model);
            }
            id = result.SurveyId!.Value;
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Survey save failed for {SurveyId}: {Reason}", model.Id, ex.Message);
            ModelState.AddModelError(string.Empty, localizer["Surveys_Validation_SaveFailed"]);
            await PrepareBuilderForRenderAsync(model, ct);
            return View("Builder", model);
        }

        // The save is already committed: a translation failure must not re-render the builder as unsaved (a re-submit
        // would double-create), so it reports and redirects.
        if (string.Equals(submitAction, "save-translate", StringComparison.Ordinal))
        {
            await ReportTranslationPassAsync(id, CurrentViewer(actorId.Value), ct);
        }
        else
        {
            SetSuccess(localizer[model.Id is null ? "Surveys_Created" : "Surveys_Saved"]);
        }

        // Send is BoardOrAdmin: an author who posts save-review anyway lands back on the
        // builder rather than on a 403.
        return string.Equals(submitAction, "save-review", StringComparison.Ordinal)
                && RoleChecks.IsAdminOrBoard(User)
            ? RedirectToAction(nameof(Send), new { id })
            : RedirectToAction(nameof(Edit), new { id });
    }

    private void ValidateAudience(SurveyBuilderViewModel model)
    {
        if (model.AudienceType == SurveyAudienceType.Team && model.AudienceTeamId is null)
        {
            ModelState.AddModelError(nameof(model.AudienceTeamId),
                localizer["Surveys_Admin_Builder_Error_TeamRequired"]);
        }

        if (model.AudienceType == SurveyAudienceType.LoggedInSince && model.AudienceLoggedInSince is null)
        {
            ModelState.AddModelError(nameof(model.AudienceLoggedInSince),
                localizer["Surveys_Admin_Builder_Error_LoggedInSinceRequired"]);
        }
    }

    /// <summary>Runs after the save commits, so a failure reports and lets the caller redirect.</summary>
    private async Task ReportTranslationPassAsync(Guid id, SurveyViewer viewer, CancellationToken ct)
    {
        try
        {
            var filled = await surveyService.PreFillTranslationsAsync(
                id, CultureCatalog.SupportedCultureCodes, viewer, ct);
            SetSuccess(filled > 0
                ? string.Format(
                    CultureInfo.CurrentCulture,
                    localizer["Surveys_Admin_Builder_Saved_TranslationsFilled"].Value, filled)
                : localizer["Surveys_Admin_Builder_Saved_NoTranslationsNeeded"].Value);
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning("Survey translation failed for {SurveyId}: {Reason}", id, ex.Message);
            SetError(localizer["Surveys_Admin_Builder_Saved_TranslationFailed"]);
        }
    }

    [HttpPost("Open/{id:guid}")]
    [Authorize(Policy = PolicyNames.BoardOrAdmin)]
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
    [Authorize(Policy = PolicyNames.BoardOrAdmin)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Close(Guid id, CancellationToken ct)
    {
        var actorId = GetCurrentUserId();
        if (actorId is null) return Forbid();
        await RunStatusTransitionAsync(id, () => surveyService.CloseAsync(id, actorId.Value, ct), "Survey closed.");
        return RedirectToAction(nameof(Edit), new { id });
    }

    [HttpGet("Send/{id:guid}")]
    [Authorize(Policy = PolicyNames.BoardOrAdmin)]
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
            IsAsociadoVote = detail.Editable.IsAsociadoVote,
        };
        return View(vm);
    }

    [HttpPost("Send/{id:guid}")]
    [ActionName(nameof(Send))]
    [Authorize(Policy = PolicyNames.BoardOrAdmin)]
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
        var auth = await AuthorizeViewResultsAsync(id, ct);
        if (auth is not null) return auth;

        var results = await surveyService.GetScopedResultsAsync(id, scope, ct);
        if (results is null) return NotFound();

        return View(SurveyResultsBuilder.Build(results));
    }

    [HttpPost("Results/{id:guid}/RankedAvailability")]
    [Authorize(Policy = PolicyNames.BoardOrAdmin)]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RankedAvailability(
        Guid id,
        Guid questionId,
        List<string>? unavailableValues,
        CancellationToken ct)
    {
        var actorId = GetCurrentUserId();
        if (actorId is null) return Forbid();
        try
        {
            await surveyService.SetRankedAvailabilityAsync(
                id,
                questionId,
                unavailableValues ?? [],
                CurrentViewer(actorId.Value),
                ct);
            SetSuccess("Candidate availability updated; ranked results were recalculated.");
        }
        catch (InvalidOperationException ex)
        {
            logger.LogWarning(
                "Ranked availability update rejected for survey {SurveyId}, question {QuestionId}: {Reason}",
                id, questionId, ex.Message);
            SetError(ex.Message);
        }
        return RedirectToAction(nameof(Results), new { id });
    }

    [HttpGet("Results/{id:guid}/Export.csv")]
    public async Task<IActionResult> ExportCsv(Guid id, CancellationToken ct)
    {
        var auth = await AuthorizeViewResultsAsync(id, ct);
        if (auth is not null) return auth;

        if (GetCurrentUserId() is not { } actorUserId) return Unauthorized();
        var export = await surveyService.GetResponseExportAsync(id, actorUserId, ct);
        if (export is null) return NotFound();

        var bytes = SurveyCsvExportBuilder.Build(export);
        return File(bytes, "text/csv", $"survey-{id}.csv");
    }

    [HttpGet("Results/{id:guid}/Export.json")]
    public async Task<IActionResult> ExportJson(Guid id, CancellationToken ct)
    {
        var auth = await AuthorizeViewResultsAsync(id, ct);
        if (auth is not null) return auth;

        if (GetCurrentUserId() is not { } actorUserId) return Unauthorized();
        var export = await surveyService.GetResponseExportAsync(id, actorUserId, ct);
        if (export is null) return NotFound();

        return File(SurveyJsonExportBuilder.Build(export), "application/json", $"survey-{id}.json");
    }

    /// <summary>
    /// Board/Admin see every survey; the author only their own after it closes. Returns the 404/403 to short-circuit
    /// on, or null to proceed.
    /// </summary>
    private async Task<IActionResult?> AuthorizeViewResultsAsync(Guid id, CancellationToken ct)
    {
        var detail = await surveyService.GetForEditAsync(id, ct);
        if (detail is null) return NotFound();

        var auth = await authorizationService.AuthorizeAsync(
            User, detail, new SurveyOperationRequirement(SurveyOperation.ViewResults));
        return auth.Succeeded ? null : Forbid();
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

    private async Task PrepareBuilderForRenderAsync(
        SurveyBuilderViewModel model,
        CancellationToken ct)
    {
        model.Teams = await LoadTeamsAsync(ct);
        model.HasSavedAnswers = model.Id is { } id
            && await surveyService.HasSavedAnswersAsync(id, ct);
        model.IsBoardOrAdmin = RoleChecks.IsAdminOrBoard(User);
        var detail = model.Id.HasValue ? await surveyService.GetForEditAsync(model.Id.Value, ct) : null;
        model.CanSubmit = detail is not null && (await authorizationService.AuthorizeAsync(
            User, detail, new SurveyOperationRequirement(SurveyOperation.Submit))).Succeeded;
    }

}
