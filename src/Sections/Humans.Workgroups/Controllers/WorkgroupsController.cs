using Humans.Workgroups.Authorization;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Controllers;
using Humans.Teams.Contracts;
using Humans.Users.Contracts;
using Humans.Workgroups.Domain;
using Humans.Workgroups.Models;
using Humans.Workgroups.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using NodaTime;

namespace Humans.Workgroups.Controllers;

/// <summary>
/// Member pages and actions. Authorizes resources, dispatches to the service and maps errors.
/// </summary>
[Authorize(Policy = PolicyNames.AppAccess)]
[Route("Workgroups")]
internal sealed class WorkgroupsController(
    IWorkgroupService workgroups,
    IUserServiceRead users,
    ITeamServiceRead teams,
    IStringLocalizer<WorkgroupsResource> localizer,
    IClock clock,
    IAuthorizationService authorization,
    ILogger<WorkgroupsController> logger) : HumansControllerBase(users)
{
    // ── The register ──────────────────────────────────────────────────────

    [HttpGet("")]
    public async Task<IActionResult> Index(CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;

        var register = await workgroups.GetRegisterAsync(ct);
        var people = await PeopleAsync(register, ct);
        return View(RegisterViewModel.Build(register, people, user.Id, clock.GetCurrentInstant()));
    }

    [HttpGet("Apply")]
    public async Task<IActionResult> Apply(CancellationToken ct)
    {
        var (error, _) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;
        return View(new WorkgroupFormViewModel());
    }

    [HttpPost("Apply")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Apply(WorkgroupFormViewModel model, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;
        if (!ModelState.IsValid) return View(model);

        return await FormAsync(model, async () =>
        {
            var id = await workgroups.ApplyAsync(user.Id, model.ToApplication(), ct);
            var created = await workgroups.GetByIdAsync(id, ct);
            return RedirectToAction(nameof(Details), new { slug = created?.Slug ?? id.ToString() });
        }, "Workgroups_Applied");
    }

    // ── One group ─────────────────────────────────────────────────────────

    [HttpGet("{slug}")]
    public async Task<IActionResult> Details(string slug, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;

        if (await ResolveAsync(slug, ct) is not { } workgroup) return NotFound();

        var people = await PeopleAsync([workgroup], ct);
        var board = await teams.GetTeamAsync(SystemTeamIds.Board, ct);

        return View(new WorkgroupPageViewModel
        {
            Workgroup = workgroup,
            Now = clock.GetCurrentInstant(),
            CurrentUserId = user.Id,
            People = people,
            BoardUserIds = board?.Members.Select(m => m.UserId).ToHashSet() ?? [],
            IsMember = workgroup.IsMember(user.Id),
            IsCoordinator = workgroup.CoordinatorUserIds().Contains(user.Id),
            CanAdminister = await MayAdministerAsync(workgroup),
            CanDoMemberWork = await MayDoMemberWorkAsync(workgroup)
        });
    }

    [HttpPost("{slug}/Join")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Join(string slug, CancellationToken ct) =>
        ActAsync(slug, (id, userId) => workgroups.JoinAsync(id, userId, ct), "Workgroups_Joined", ct,
            memberOnly: false);

    [HttpPost("{slug}/Leave")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Leave(string slug, Guid? replacementCoordinatorUserId, CancellationToken ct) =>
        ActAsync(slug,
            (id, userId) => workgroups.LeaveAsync(id, userId, replacementCoordinatorUserId, asAdmin: false, ct),
            "Workgroups_Left", ct, memberOnly: false);

    [HttpPost("{slug}/RequestStatus")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> RequestStatus(string slug, string? question, CancellationToken ct) =>
        ActAsync(slug, (id, userId) => workgroups.RequestStatusAsync(id, userId, question, ct),
            "Workgroups_StatusRequested", ct, memberOnly: false);

    // ── Register fields and coordinators ──────────────────────────────────

    [HttpGet("{slug}/Edit")]
    public async Task<IActionResult> Edit(string slug, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;

        if (await ResolveAsync(slug, ct) is not { } workgroup) return NotFound();
        if (!await MayDoMemberWorkAsync(workgroup)) return Forbid();

        return View(WorkgroupFormViewModel.FromWorkgroup(workgroup));
    }

    [HttpPost("{slug}/Edit")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Edit(string slug, WorkgroupFormViewModel model, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;
        if (await ResolveAsync(slug, ct) is not { } workgroup) return NotFound();
        if (!await MayDoMemberWorkAsync(workgroup)) return Forbid();
        if (!ModelState.IsValid) return View(model);

        return await FormAsync(model, async () =>
        {
            await workgroups.EditRegisterAsync(workgroup.Id, user.Id, model.ToEdit(), ct);
            // The name may have re-slugged the group, so the redirect asks by id.
            var saved = await workgroups.GetByIdAsync(workgroup.Id, ct);
            return RedirectToAction(nameof(Details), new { slug = saved?.Slug ?? slug });
        }, "Workgroups_Saved");
    }

    [HttpPost("{slug}/Coordinators")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Coordinators(string slug, Guid?[] coordinatorUserIds, CancellationToken ct) =>
        ActAsync(slug,
            (id, userId) => workgroups.SetCoordinatorsAsync(id, userId, coordinatorUserIds.OfType<Guid>().ToArray(), asAdmin: false, ct),
            "Workgroups_CoordinatorsSaved", ct);

    // ── Meetings ──────────────────────────────────────────────────────────

    [HttpGet("{slug}/Meetings/Create")]
    public Task<IActionResult> CreateMeeting(string slug, CancellationToken ct) =>
        MemberFormAsync(slug, ct, w => new MeetingFormViewModel
        {
            Slug = w.Slug,
            StartUtc = clock.GetCurrentInstant(),
            EndUtc = clock.GetCurrentInstant() + Duration.FromHours(1)
        }, MeetingForm);

    [HttpGet("{slug}/Meetings/{id:guid}/Edit")]
    public Task<IActionResult> EditMeeting(string slug, Guid id, CancellationToken ct) =>
        MemberFormAsync(slug, ct, w => w.Meetings.FirstOrDefault(m => m.Id == id) is { } meeting
            ? MeetingFormViewModel.FromMeeting(w.Slug, meeting)
            : null, MeetingForm);

    [HttpPost("{slug}/Meetings/Save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveMeeting(string slug, MeetingFormViewModel model, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;
        if (await ResolveAsync(slug, ct) is not { } workgroup) return NotFound();
        if (!await MayDoMemberWorkAsync(workgroup)) return Forbid();
        if (model.Id is { } editing && !workgroup.Meetings.Any(m => m.Id == editing)) return NotFound();
        if (!ModelState.IsValid) return View(MeetingForm, model);

        return await FormAsync(model, async () =>
        {
            if (model.Id is { } id)
                await workgroups.UpdateMeetingAsync(id, user.Id, model.ToSave(), ct);
            else
                await workgroups.CreateMeetingAsync(workgroup.Id, user.Id, model.ToSave(), ct);
            return RedirectToAction(nameof(Details), new { slug = workgroup.Slug });
        }, "Workgroups_MeetingSaved", MeetingForm);
    }

    [HttpPost("{slug}/Meetings/{id:guid}/Delete")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> DeleteMeeting(string slug, Guid id, CancellationToken ct) =>
        ActAsync(slug, (_, userId) => workgroups.DeleteMeetingAsync(id, userId, ct),
            "Workgroups_MeetingDeleted", ct, owns: w => w.Meetings.Any(m => m.Id == id));

    // ── The log ───────────────────────────────────────────────────────────

    [HttpGet("{slug}/Log/Add")]
    public Task<IActionResult> AddLogEntry(string slug, CancellationToken ct) =>
        MemberFormAsync(slug, ct, w => new LogEntryFormViewModel
        {
            Slug = w.Slug,
            OccurredOn = clock.GetCurrentInstant().InUtc().Date
        }, LogEntryForm);

    [HttpGet("{slug}/Log/{id:guid}/Edit")]
    public Task<IActionResult> EditLogEntry(string slug, Guid id, CancellationToken ct) =>
        MemberFormAsync(slug, ct, w => w.LogEntries.FirstOrDefault(e => e.Id == id) is { } entry
            ? LogEntryFormViewModel.FromEntry(w.Slug, entry)
            : null, LogEntryForm);

    [HttpPost("{slug}/Log/Save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveLogEntry(string slug, LogEntryFormViewModel model, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;
        if (await ResolveAsync(slug, ct) is not { } workgroup) return NotFound();
        if (!await MayDoMemberWorkAsync(workgroup)) return Forbid();
        if (model.Id is { } editing && !workgroup.LogEntries.Any(e => e.Id == editing)) return NotFound();
        if (!ModelState.IsValid) return View(LogEntryForm, model);

        return await FormAsync(model, async () =>
        {
            if (model.Id is { } id)
                await workgroups.UpdateLogEntryAsync(id, user.Id, model.ToSave(), ct);
            else
                await workgroups.AddLogEntryAsync(workgroup.Id, user.Id, model.ToSave(), ct);
            return RedirectToAction(nameof(Details), new { slug = workgroup.Slug });
        }, "Workgroups_LogEntrySaved", LogEntryForm);
    }

    [HttpPost("{slug}/Log/{id:guid}/Delete")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> DeleteLogEntry(string slug, Guid id, CancellationToken ct) =>
        ActAsync(slug, (_, userId) => workgroups.DeleteLogEntryAsync(id, userId, ct),
            "Workgroups_LogEntryDeleted", ct, owns: w => w.LogEntries.Any(e => e.Id == id));

    // ── Surveys ───────────────────────────────────────────────────────────

    [HttpPost("{slug}/Surveys/Link")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> LinkSurvey(string slug, Guid surveyId, CancellationToken ct) =>
        ActAsync(slug, (id, userId) => workgroups.LinkSurveyAsync(id, userId, surveyId, ct),
            "Workgroups_SurveyLinked", ct);

    // ── Ending the group ──────────────────────────────────────────────────

    [HttpPost("{slug}/Done")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Done(string slug, WorkgroupDormantReason reason, CancellationToken ct) =>
        ActAsync(slug, (id, userId) => workgroups.MarkDoneAsync(id, userId, reason, ct),
            "Workgroups_MarkedDone", ct);

    // ── Documents ─────────────────────────────────────────────────────────

    [HttpGet("{slug}/Documents/{id:guid}")]
    public async Task<IActionResult> Document(string slug, Guid id, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;
        if (await ResolveAsync(slug, ct) is not { } workgroup) return NotFound();
        if (workgroup.Documents.FirstOrDefault(d => d.Id == id) is not { } document) return NotFound();

        var isMember = workgroup.IsMember(user.Id);
        var canAdminister = await MayAdministerAsync(workgroup);
        // A draft is the group's working copy; it is not the register's business yet.
        if (document.Status == WorkgroupDocumentStatus.Draft && !isMember && !canAdminister)
            return NotFound();

        return View(new DocumentPageViewModel
        {
            Workgroup = workgroup,
            Document = document,
            Now = clock.GetCurrentInstant(),
            CurrentUserId = user.Id,
            People = await PeopleAsync([workgroup], ct),
            IsMember = isMember,
            CanAdminister = canAdminister,
            CanDoMemberWork = await MayDoMemberWorkAsync(workgroup)
        });
    }

    [HttpGet("{slug}/Documents/Create")]
    public Task<IActionResult> CreateDocument(string slug, CancellationToken ct) =>
        MemberFormAsync(slug, ct, w => new DocumentFormViewModel { Slug = w.Slug }, DocumentForm);

    [HttpGet("{slug}/Documents/{id:guid}/Edit")]
    public Task<IActionResult> EditDocument(string slug, Guid id, CancellationToken ct) =>
        MemberFormAsync(slug, ct, w => w.Documents.FirstOrDefault(d => d.Id == id) is { } document
            ? DocumentFormViewModel.FromDocument(w.Slug, document)
            : null, DocumentForm);

    [HttpPost("{slug}/Documents/Save")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SaveDocument(string slug, DocumentFormViewModel model, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;
        if (await ResolveAsync(slug, ct) is not { } workgroup) return NotFound();
        if (!await MayDoMemberWorkAsync(workgroup)) return Forbid();
        if (model.Id is { } editing && !workgroup.Documents.Any(d => d.Id == editing)) return NotFound();
        if (!ModelState.IsValid) return View(DocumentForm, model);

        return await FormAsync(model, async () =>
        {
            var id = model.Id;
            if (id is { } existing)
                await workgroups.UpdateDocumentAsync(existing, user.Id, model.ToSave(), ct);
            else
                id = await workgroups.CreateDocumentAsync(workgroup.Id, user.Id, model.ToSave(), ct);
            return RedirectToAction(nameof(Document), new { slug = workgroup.Slug, id });
        }, "Workgroups_DocumentSaved", DocumentForm);
    }

    [HttpPost("{slug}/Documents/{id:guid}/Publish")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> PublishDocument(string slug, Guid id, CancellationToken ct) =>
        ActAsync(slug, (_, userId) => workgroups.PublishDocumentAsync(id, userId, ct),
            "Workgroups_DocumentPublished", ct, documentId: id, owns: OwnsDocument(id));

    [HttpPost("{slug}/Documents/{id:guid}/OpenComments")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> OpenComments(
        string slug, Guid id, Instant opensAt, Instant closesAt, string categories, CancellationToken ct) =>
        ActAsync(slug, (_, userId) => workgroups.OpenCommentsAsync(id, userId,
                new WorkgroupCommentWindow(opensAt, closesAt, SplitCategories(categories)), ct),
            "Workgroups_CommentsOpened", ct, documentId: id, owns: OwnsDocument(id));

    [HttpPost("{slug}/Documents/{id:guid}/CloseComments")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> CloseComments(string slug, Guid id, CancellationToken ct) =>
        ActAsync(slug, (_, userId) => workgroups.CloseCommentsAsync(id, userId, ct),
            "Workgroups_CommentsClosed", ct, documentId: id, owns: OwnsDocument(id));

    [HttpPost("{slug}/Documents/{id:guid}/Deliver")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> DeliverDocument(string slug, Guid id, CancellationToken ct) =>
        ActAsync(slug, (_, userId) => workgroups.DeliverDocumentAsync(id, userId, ct),
            "Workgroups_DocumentDelivered", ct, documentId: id, owns: OwnsDocument(id));

    // ── Comments ──────────────────────────────────────────────────────────

    [HttpPost("{slug}/Documents/{id:guid}/Comments")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> AddComment(
        string slug, Guid id, string category, string body, CancellationToken ct) =>
        ActAsync(slug, (_, userId) => workgroups.AddCommentAsync(id, userId, category, body, ct),
            "Workgroups_CommentAdded", ct, documentId: id, memberOnly: false, owns: OwnsDocument(id));

    [HttpPost("{slug}/Documents/{id:guid}/Comments/RespondCategory")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> RespondToCategory(
        string slug,
        Guid id,
        string category,
        WorkgroupCommentDisposition disposition,
        string? response,
        CancellationToken ct) =>
        ActAsync(slug,
            (_, userId) => workgroups.RespondToCategoryAsync(id, userId, category, disposition, response, ct),
            "Workgroups_CommentsAnswered", ct, documentId: id, owns: OwnsDocument(id));

    [HttpPost("{slug}/Comments/{id:guid}/Respond")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> RespondToComment(
        string slug,
        Guid id,
        Guid documentId,
        WorkgroupCommentDisposition disposition,
        string? response,
        CancellationToken ct) =>
        ActAsync(slug, (_, userId) => workgroups.RespondToCommentAsync(id, userId, disposition, response, ct),
            "Workgroups_CommentAnswered", ct, documentId: documentId, owns: OwnsComment(documentId, id));

    [HttpPost("{slug}/Comments/{id:guid}/Hide")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> HideComment(
        string slug, Guid id, Guid documentId, string reason, CancellationToken ct) =>
        ActAsync(slug, (_, userId) => workgroups.HideCommentAsync(id, userId, reason, ct),
            "Workgroups_CommentHidden", ct, documentId: documentId, owns: OwnsComment(documentId, id));

    // ── Plumbing ──────────────────────────────────────────────────────────

    /// <summary>
    /// Slug routes also answer to the group's id, so a link that pre-dates a rename still
    /// lands (memory/architecture/slug-routes-fallback-to-guid.md).
    /// </summary>
    private async Task<WorkgroupInfo?> ResolveAsync(string slug, CancellationToken ct) =>
        Guid.TryParse(slug, out var id)
            ? await workgroups.GetByIdAsync(id, ct)
            : await workgroups.GetBySlugAsync(slug, ct);

    private async Task<bool> MayAdministerAsync(WorkgroupInfo workgroup) =>
        (await authorization.AuthorizeAsync(User, workgroup, WorkgroupOperationRequirement.Administer)).Succeeded;

    private async Task<bool> MayDoMemberWorkAsync(WorkgroupInfo workgroup) =>
        (await authorization.AuthorizeAsync(User, workgroup, WorkgroupOperationRequirement.Member)).Succeeded;

    /// <summary>The nested-resource route checks: the id has to belong to this group.</summary>
    private static Func<WorkgroupInfo, bool> OwnsDocument(Guid documentId) =>
        w => w.Documents.Any(d => d.Id == documentId);

    private static Func<WorkgroupInfo, bool> OwnsComment(Guid documentId, Guid commentId) =>
        w => w.Documents.Any(d => d.Id == documentId && d.Comments.Any(c => c.Id == commentId));

    private static IReadOnlyList<string> SplitCategories(string? categories) =>
        (categories ?? string.Empty)
            .Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    /// <summary>Burner names and tiers for every person named anywhere on the page.</summary>
    private async Task<IReadOnlyDictionary<Guid, UserInfo>> PeopleAsync(
        IReadOnlyList<WorkgroupInfo> register, CancellationToken ct)
    {
        var ids = register
            .SelectMany(w => w.Members.Select(m => m.UserId)
                .Concat(w.LogEntries.Select(e => e.AuthorUserId).OfType<Guid>())
                .Concat(w.Documents.SelectMany(d => d.Comments.Select(c => c.AuthorUserId).OfType<Guid>()))
                .Concat(w.AppliedByUserId is { } applicant ? new[] { applicant } : []))
            .Distinct()
            .ToList();

        return ids.Count == 0
            ? new Dictionary<Guid, UserInfo>()
            : await UserService.GetUserInfosAsync(ids, ct);
    }

    /// <summary>A GET that renders a member-only form, or 403/404 before it.</summary>
    private async Task<IActionResult> MemberFormAsync<TModel>(
        string slug, CancellationToken ct, Func<WorkgroupInfo, TModel?> build, string viewName)
        where TModel : class
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;
        if (await ResolveAsync(slug, ct) is not { } workgroup) return NotFound();
        if (!await MayDoMemberWorkAsync(workgroup)) return Forbid();

        return build(workgroup) is { } model ? View(viewName, model) : NotFound();
    }

    /// <summary>Authorize the group and nested resource before mutating, then redirect with feedback.</summary>
    private async Task<IActionResult> ActAsync(
        string slug,
        Func<Guid, Guid, Task> action,
        string successKey,
        CancellationToken ct,
        Guid? documentId = null,
        bool memberOnly = true,
        Func<WorkgroupInfo, bool>? owns = null)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;
        if (await ResolveAsync(slug, ct) is not { } workgroup) return NotFound();
        if (memberOnly && !await MayDoMemberWorkAsync(workgroup)) return Forbid();
        if (owns is not null && !owns(workgroup)) return NotFound();

        try
        {
            await action(workgroup.Id, user.Id);
            SetSuccess(localizer[successKey]);
        }
        catch (KeyNotFoundException ex)
        {
            logger.LogInformation(ex, "Workgroups {Action}: not found", ActionName());
            return NotFound();
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "Workgroups {Action}: forbidden", ActionName());
            return Forbid();
        }
        catch (WorkgroupRuleException ex)
        {
            logger.LogInformation(ex, "Workgroups {Action}: rule {Rule}", ActionName(), ex.Key);
            SetError(localizer[ex.Key, ex.Args]);
        }

        return documentId is { } id
            ? RedirectToAction(nameof(Document), new { slug = workgroup.Slug, id })
            : RedirectToAction(nameof(Details), new { slug = workgroup.Slug });
    }

    /// <summary>Form POSTs: a rule re-renders the form with its localized message.</summary>
    private async Task<IActionResult> FormAsync(
        object model, Func<Task<IActionResult>> action, string successKey, string? viewName = null)
    {
        try
        {
            var result = await action();
            SetSuccess(localizer[successKey]);
            return result;
        }
        catch (KeyNotFoundException ex)
        {
            logger.LogInformation(ex, "Workgroups {Action}: not found", ActionName());
            return NotFound();
        }
        catch (UnauthorizedAccessException ex)
        {
            logger.LogWarning(ex, "Workgroups {Action}: forbidden", ActionName());
            return Forbid();
        }
        catch (WorkgroupRuleException ex)
        {
            logger.LogInformation(ex, "Workgroups {Action}: rule {Rule}", ActionName(), ex.Key);
            ModelState.AddModelError(string.Empty, localizer[ex.Key, ex.Args]);
            return viewName is null ? View(model) : View(viewName, model);
        }
    }

    private string? ActionName() => ControllerContext.ActionDescriptor.ActionName;

    // View names for the shared forms, so the string lives once.
    private const string MeetingForm = "MeetingForm";
    private const string LogEntryForm = "LogEntryForm";
    private const string DocumentForm = "DocumentForm";
}
