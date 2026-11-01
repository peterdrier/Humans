using Microsoft.AspNetCore.Mvc.ModelBinding;
using Humans.Workgroups.Authorization;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Base.Controllers;
using Humans.Governance.Contracts;
using Humans.Holded.Contracts;
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

[Authorize(Policy = PolicyNames.AppAccess)]
[Route("Workgroups")]
internal sealed class WorkgroupsController(
    IWorkgroupService workgroups,
    IUserService users,
    ITeamService teams,
    IGovernanceService governance,
    IStringLocalizer<WorkgroupsResource> localizer,
    IClock clock,
    IAuthorizationService authorization,
    ILogger<WorkgroupsController> logger) : HumansControllerBase(users)
{
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
            var mutation = await workgroups.ApplyAsync(user.Id, model.ToApplication(), ct);
            if (mutation.Refusal is not null) return (mutation, null);
            var id = mutation.Value;
            var created = await workgroups.GetByIdAsync(id, ct);
            return (mutation, RedirectToAction(nameof(Details), new { slug = created?.Slug ?? id.ToString() }));
        }, "Workgroups_Applied");
    }

    [HttpGet("{slug}")]
    public async Task<IActionResult> Details(string slug, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;

        if (await ResolveAsync(slug, ct) is not { } workgroup) return NotFound();

        var people = await PeopleAsync([workgroup], ct);
        var board = await teams.GetTeamAsync(SystemTeamIds.Board, ct);
        var canAdminister = await MayAdministerAsync(workgroup);
        var tiers = await governance.GetMembershipTiersAsync(ct);
        var canSeeBudget = canAdminister
            || tiers.GetValueOrDefault(user.Id) is MembershipTier.Colaborador or MembershipTier.Asociado;
        IReadOnlyList<HoldedChartAccountInfo> accounts = canAdminister
            ? await workgroups.ListExpenseAccountsAsync(ct)
            : [];

        return View(new WorkgroupPageViewModel
        {
            Workgroup = workgroup,
            Now = clock.GetCurrentInstant(),
            CurrentUserId = user.Id,
            People = people,
            Tiers = tiers,
            BoardUserIds = board?.Members.Select(m => m.UserId).ToHashSet() ?? [],
            IsMember = workgroup.IsMember(user.Id),
            IsCoordinator = workgroup.IsCoordinator(user.Id),
            IsPendingMember = workgroup.IsPendingMember(user.Id),
            CanAdminister = canAdminister,
            CanDoMemberWork = await MayDoMemberWorkAsync(workgroup),
            CanSeeBudget = canSeeBudget,
            ExpenseAccounts = accounts
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

    [HttpPost("{slug}/Members/{userId:guid}/Approve")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> ApproveMember(string slug, Guid userId, CancellationToken ct) =>
        ManageMembersAsync(slug,
            (id, actorId, asAdmin) => workgroups.ApproveMemberAsync(id, actorId, userId, asAdmin, ct),
            "Workgroups_MemberApproved", ct);

    [HttpPost("{slug}/Members/{userId:guid}/Decline")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> DeclineMember(string slug, Guid userId, CancellationToken ct) =>
        ManageMembersAsync(slug,
            (id, actorId, asAdmin) => workgroups.DeclineMemberAsync(id, actorId, userId, asAdmin, ct),
            "Workgroups_MemberDeclined", ct);

    [HttpPost("{slug}/Members/Add")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> AddMember(string slug, Guid? userId, CancellationToken ct) =>
        ManageMembersAsync(slug,
            (id, actorId, asAdmin) => userId is { } added
                ? workgroups.AddMemberAsync(id, actorId, added, asAdmin, ct)
                : Task.FromResult(new WorkgroupMutationResult(new WorkgroupRefusal(WorkgroupErrorKeys.NotFound))),
            "Workgroups_MemberAdded", ct);

    [HttpGet("{slug}/Edit")]
    public async Task<IActionResult> Edit(string slug, CancellationToken ct)
    {
        var (error, _) = await ResolveCurrentUserOrChallengeAsync(ct);
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
            var mutation = await workgroups.EditRegisterAsync(workgroup.Id, user.Id, model.ToEdit(), ct);
            if (mutation.Refusal is not null) return (mutation, null);
            // The name may have re-slugged the group, so the redirect asks by id.
            var saved = await workgroups.GetByIdAsync(workgroup.Id, ct);
            return (mutation, RedirectToAction(nameof(Details), new { slug = saved?.Slug ?? slug }));
        }, "Workgroups_Saved");
    }

    [HttpPost("{slug}/Coordinators")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Coordinators(string slug, Guid?[] coordinatorUserIds, CancellationToken ct) =>
        ActAsync(slug,
            (id, userId) => workgroups.SetCoordinatorsAsync(id, userId, coordinatorUserIds.OfType<Guid>().ToArray(), asAdmin: false, ct),
            "Workgroups_CoordinatorsSaved", ct);

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
            WorkgroupMutationResult mutation;
            if (model.Id is { } id)
                mutation = await workgroups.UpdateMeetingAsync(id, user.Id, model.ToSave(), ct);
            else
                mutation = await workgroups.CreateMeetingAsync(workgroup.Id, user.Id, model.ToSave(), ct);
            return (mutation, RedirectToAction(nameof(Details), new { slug = workgroup.Slug }));
        }, "Workgroups_MeetingSaved", MeetingForm);
    }

    [HttpPost("{slug}/Meetings/{id:guid}/Delete")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> DeleteMeeting(string slug, Guid id, CancellationToken ct) =>
        ActAsync(slug, (_, userId) => workgroups.DeleteMeetingAsync(id, userId, ct),
            "Workgroups_MeetingDeleted", ct, owns: w => w.Meetings.Any(m => m.Id == id));

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
            WorkgroupMutationResult mutation;
            if (model.Id is { } id)
                mutation = await workgroups.UpdateLogEntryAsync(id, user.Id, model.ToSave(), ct);
            else
                mutation = await workgroups.AddLogEntryAsync(workgroup.Id, user.Id, model.ToSave(), ct);
            return (mutation, RedirectToAction(nameof(Details), new { slug = workgroup.Slug }));
        }, "Workgroups_LogEntrySaved", LogEntryForm);
    }

    [HttpPost("{slug}/Log/{id:guid}/Delete")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> DeleteLogEntry(string slug, Guid id, CancellationToken ct) =>
        ActAsync(slug, (_, userId) => workgroups.DeleteLogEntryAsync(id, userId, ct),
            "Workgroups_LogEntryDeleted", ct, owns: w => w.LogEntries.Any(e => e.Id == id));

    [HttpPost("{slug}/Surveys/Link")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> LinkSurvey(string slug, Guid surveyId, CancellationToken ct) =>
        ActAsync(slug, (id, userId) => workgroups.LinkSurveyAsync(id, userId, surveyId, ct),
            "Workgroups_SurveyLinked", ct);

    [HttpPost("{slug}/Done")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> Done(string slug, [BindRequired] WorkgroupDormantReason reason, CancellationToken ct) =>
        ActAsync(slug, (id, userId) => workgroups.MarkDoneAsync(id, userId, reason, ct),
            "Workgroups_MarkedDone", ct);

    [HttpGet("{slug}/Documents/{id:guid}")]
    public async Task<IActionResult> Document(string slug, Guid id, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;
        if (await ResolveAsync(slug, ct) is not { } workgroup) return NotFound();
        if (workgroup.Documents.FirstOrDefault(d => d.Id == id) is not { } document) return NotFound();

        var isMember = workgroup.IsMember(user.Id);
        var canAdminister = await MayAdministerAsync(workgroup);
        // A draft isn't the register's business yet; a sensitive group's documents never are.
        if ((document.Status == WorkgroupDocumentStatus.Draft || workgroup.IsSensitive) && !isMember && !canAdminister)
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
            WorkgroupMutationResult mutation;
            if (id is { } existing)
                mutation = await workgroups.UpdateDocumentAsync(existing, user.Id, model.ToSave(), ct);
            else
            {
                var created = await workgroups.CreateDocumentAsync(workgroup.Id, user.Id, model.ToSave(), ct);
                mutation = created;
                id = created.Value;
            }
            return (mutation, RedirectToAction(nameof(Document), new { slug = workgroup.Slug, id }));
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
        string slug, Guid id, Instant opensAt, Instant closesAt, string? categories, CancellationToken ct) =>
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

    [HttpPost("{slug}/Documents/{id:guid}/Comments")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> AddComment(
        string slug, Guid id, string? category, string? body, CancellationToken ct) =>
        ActAsync(slug, (_, userId) => workgroups.AddCommentAsync(id, userId, category ?? string.Empty, body ?? string.Empty, ct),
            "Workgroups_CommentAdded", ct, documentId: id, memberOnly: false, owns: OwnsDocument(id));

    [HttpPost("{slug}/Documents/{id:guid}/Comments/RespondCategory")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> RespondToCategory(
        string slug,
        Guid id,
        string? category,
        [BindRequired] WorkgroupCommentDisposition disposition,
        string? response,
        CancellationToken ct) =>
        ActAsync(slug,
            (_, userId) => workgroups.RespondToCategoryAsync(id, userId, category ?? string.Empty, disposition, response, ct),
            "Workgroups_CommentsAnswered", ct, documentId: id, owns: OwnsDocument(id));

    [HttpPost("{slug}/Comments/{id:guid}/Respond")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> RespondToComment(
        string slug,
        Guid id,
        Guid documentId,
        [BindRequired] WorkgroupCommentDisposition disposition,
        string? response,
        CancellationToken ct) =>
        ActAsync(slug, (_, userId) => workgroups.RespondToCommentAsync(id, userId, disposition, response, ct),
            "Workgroups_CommentAnswered", ct, documentId: documentId, owns: OwnsComment(documentId, id));

    [HttpPost("{slug}/Comments/{id:guid}/Hide")]
    [ValidateAntiForgeryToken]
    public Task<IActionResult> HideComment(
        string slug, Guid id, Guid documentId, string? reason, CancellationToken ct) =>
        ActAsync(slug, (_, userId) => workgroups.HideCommentAsync(id, userId, reason ?? string.Empty, ct),
            "Workgroups_CommentHidden", ct, documentId: documentId, owns: OwnsComment(documentId, id));

    /// <summary>Also answers to the group's id, so a link from before a rename still lands.</summary>
    private async Task<WorkgroupInfo?> ResolveAsync(string slug, CancellationToken ct) =>
        Guid.TryParse(slug, out var id)
            ? await workgroups.GetByIdAsync(id, ct)
            : await workgroups.GetBySlugAsync(slug, ct);

    private async Task<bool> MayAdministerAsync(WorkgroupInfo workgroup) =>
        (await authorization.AuthorizeAsync(User, workgroup, WorkgroupOperationRequirement.Administer)).Succeeded;

    private async Task<bool> MayDoMemberWorkAsync(WorkgroupInfo workgroup) =>
        (await authorization.AuthorizeAsync(User, workgroup, WorkgroupOperationRequirement.Member)).Succeeded;

    /// <summary>The nested id must belong to this group.</summary>
    private static Func<WorkgroupInfo, bool> OwnsDocument(Guid documentId) =>
        w => w.Documents.Any(d => d.Id == documentId);

    private static Func<WorkgroupInfo, bool> OwnsComment(Guid documentId, Guid commentId) =>
        w => w.Documents.Any(d => d.Id == documentId && d.Comments.Any(c => c.Id == commentId));

    private static IReadOnlyList<string> SplitCategories(string? categories) =>
        (categories ?? string.Empty)
            .Split([',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    private async Task<IReadOnlyDictionary<Guid, UserInfo>> PeopleAsync(
        IReadOnlyList<WorkgroupInfo> register, CancellationToken ct)
    {
        // Every attribution the pages render, not just memberships: a Board member who writes a document or minutes
        // a meeting is named there without ever joining.
        var ids = register
            .SelectMany(w => w.Members.Select(m => m.UserId)
                .Concat(w.LogEntries.Select(e => e.AuthorUserId).OfType<Guid>())
                .Concat(w.Meetings.Select(m => m.CreatedByUserId).OfType<Guid>())
                .Concat(w.Documents.Select(d => d.CreatedByUserId).OfType<Guid>())
                .Concat(w.Documents.Select(d => d.UpdatedByUserId).OfType<Guid>())
                .Concat(w.Documents.Select(d => d.DispositionByUserId).OfType<Guid>())
                .Concat(w.Documents.SelectMany(d => d.Comments.Select(c => c.AuthorUserId).OfType<Guid>()))
                .Concat(w.AppliedByUserId is { } applicant ? new[] { applicant } : []))
            .Distinct()
            .ToList();

        return ids.Count == 0
            ? new Dictionary<Guid, UserInfo>()
            : await UserService.GetUserInfosAsync(ids, ct);
    }

    private async Task<IActionResult> MemberFormAsync<TModel>(
        string slug, CancellationToken ct, Func<WorkgroupInfo, TModel?> build, string viewName)
        where TModel : class
    {
        var (error, _) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;
        if (await ResolveAsync(slug, ct) is not { } workgroup) return NotFound();
        if (!await MayDoMemberWorkAsync(workgroup)) return Forbid();

        return build(workgroup) is { } model ? View(viewName, model) : NotFound();
    }

    private async Task<IActionResult> ActAsync<T>(
        string slug,
        Func<Guid, Guid, Task<T>> action,
        string successKey,
        CancellationToken ct,
        Guid? documentId = null,
        bool memberOnly = true,
        Func<WorkgroupInfo, bool>? owns = null) where T : WorkgroupMutationResult
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;
        if (await ResolveAsync(slug, ct) is not { } workgroup) return NotFound();
        if (memberOnly && !await MayDoMemberWorkAsync(workgroup)) return Forbid();
        if (owns is not null && !owns(workgroup)) return NotFound();

        if (!ModelState.IsValid)
        {
            logger.LogWarning("Rejected malformed workgroup {Action} input for user {UserId}, workgroup {WorkgroupId}",
                ActionName(), user.Id, workgroup.Id);
            return BadRequest(ModelState);
        }

        Flash(await action(workgroup.Id, user.Id), successKey);

        return documentId is { } id
            ? RedirectToAction(nameof(Document), new { slug = workgroup.Slug, id })
            : RedirectToAction(nameof(Details), new { slug = workgroup.Slug });
    }

    /// <summary>Board/Admin passes <c>asAdmin</c>, so the service skips its own coordinator check.</summary>
    private async Task<IActionResult> ManageMembersAsync(
        string slug, Func<Guid, Guid, bool, Task<WorkgroupMutationResult>> action, string successKey, CancellationToken ct)
    {
        var (error, user) = await ResolveCurrentUserOrChallengeAsync(ct);
        if (error is not null) return error;
        if (await ResolveAsync(slug, ct) is not { } workgroup) return NotFound();
        var asAdmin = await MayAdministerAsync(workgroup);
        if (!asAdmin && !(workgroup.IsCoordinator(user.Id) && await MayDoMemberWorkAsync(workgroup))) return Forbid();

        Flash(await action(workgroup.Id, user.Id, asAdmin), successKey);
        return RedirectToAction(nameof(Details), new { slug = workgroup.Slug });
    }

    private async Task<IActionResult> FormAsync(
        object model, Func<Task<(WorkgroupMutationResult Mutation, IActionResult? Response)>> action,
        string successKey, string? viewName = null)
    {
        var (mutation, response) = await action();
        if (mutation.Refusal is { } refusal)
        {
            logger.LogWarning("Workgroups {Action}: rule {Rule}", ActionName(), refusal.Key);
            ModelState.AddModelError(string.Empty, localizer[refusal.Key, refusal.Args]);
            return viewName is null ? View(model) : View(viewName, model);
        }

        SetSuccess(localizer[successKey]);
        return response!;
    }

    private void Flash(WorkgroupMutationResult result, string successKey)
    {
        if (result.Refusal is { } refusal)
        {
            logger.LogWarning("Workgroups {Action}: rule {Rule}", ActionName(), refusal.Key);
            SetError(localizer[refusal.Key, refusal.Args]);
        }
        else
        {
            SetSuccess(localizer[successKey]);
        }
    }

    private string? ActionName() => ControllerContext.ActionDescriptor.ActionName;

    private const string MeetingForm = "MeetingForm";
    private const string LogEntryForm = "LogEntryForm";
    private const string DocumentForm = "DocumentForm";
}
