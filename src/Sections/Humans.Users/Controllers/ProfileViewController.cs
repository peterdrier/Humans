using Humans.Users.Services;
using Humans.Base.Attributes;
using Humans.Base.Models.Tables;
// @e2e: board.spec.ts
// @e2e: profile.spec.ts
using Humans.Base.Controllers;
using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Web;
using AngleSharp.Dom;
using Humans.Users.Authorization;
using Humans.Base.Configuration;
using Microsoft.Extensions.Configuration;
using Humans.Base.Extensions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Localization;
using Humans.Gdpr.Contracts;
using Humans.Base.Constants;
using Humans.Base.Enums;
using Humans.Users.Models;
using Microsoft.Extensions.Options;
using NodaTime;
using Humans.AuditLog.Contracts;
using Humans.Campaigns.Contracts;
using Humans.Camps.Contracts;
using Humans.Email.Contracts;
using Humans.Shifts.Contracts;
using Humans.Teams.Contracts;
using Humans.Tickets.Contracts;
using Humans.Onboarding.Contracts;
using Humans.Governance.Contracts;
using Humans.Users.Contracts;
using Humans.Base;
using Humans.Base.Authorization;

namespace Humans.Users.Controllers;

// Other members' profiles: the profile page, picture, popovers, in-platform messaging and
// search. Split out of ProfileController, which keeps the member's own profile pages;
// ProfileEmailsController holds the email grids.
[Authorize]
[Route("Profile")]
internal sealed class ProfileViewController(
    IUserServiceInternal userService,
    IProfilePictureService profilePictureService,
    IEmailService emailService,
    IEmailMessageFactory emailMessages,
    ICommunicationPreferenceService commPrefService,
    IAuditLogService auditLogService,
    IShiftSignups shiftSignupService,
    IBurnSettingsService burnSettings,
    IShiftManagementServiceRead shiftMgmt,
    IStringLocalizer<UsersResource> localizer,
    IStringLocalizer<SharedResource> sharedLocalizer,
    ITeamServiceRead teamService,
    ITeamMessageOptionsProvider teamMessageOptionsProvider,
    ICampServiceRead campService,
    IAuthorizationService authorizationService) : HumansControllerBase(userService)
{
    private readonly IUserServiceInternal _userService = userService;

    // ─── Shared (Profile Picture) ────────────────────────────────────

    [HttpGet("Picture")]
    [AllowAnonymous]
    [ResponseCache(Duration = 3600, Location = ResponseCacheLocation.Client)]
    public async Task<IActionResult> Picture(Guid id, CancellationToken ct)
    {
        // Controller routes through the profile-picture service, which owns the FS read path + GDPR gate.
        var result = await profilePictureService.GetProfilePictureAsync(id, ct);
        if (result is null)
        {
            return NotFound();
        }

        return File(result.Value.Data, result.Value.ContentType);
    }

    // ─── View Another Profile ────────────────────────────────────────

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> ViewProfile(Guid id, CancellationToken ct)
    {
        var profileInfo = await _userService.GetUserInfoAsync(id, ct);
        var profile = profileInfo?.Profile;

        if (profile is null || profileInfo!.IsSuspended)
        {
            return NotFound();
        }

        // A link to a merged-away id lands on the survivor's page, under the survivor's URL.
        if (profileInfo.Id != id)
            return RedirectToAction(nameof(ViewProfile), new { id = profileInfo.Id });

        var viewer = await GetCurrentUserInfoAsync(ct);
        if (viewer is null)
        {
            return NotFound();
        }

        var isOwnProfile = viewer.Id == id;

        var noShowContext = await BuildNoShowHistoryContextAsync(id, viewer.Id, isOwnProfile, ct);

        // Onsite chip: self always, plus the admin/board policy gating /Tickets/Admin/Onsite.
        var canViewOnsiteChip = isOwnProfile
            || (await authorizationService.AuthorizeAsync(
                User, PolicyNames.TicketAdminBoardOrAdmin)).Succeeded;

        var canViewSentMessages = await CanViewSentMessagesAsync(viewer.Id, isOwnProfile);
        var teamMessageOptions = !isOwnProfile
            && await commPrefService.AcceptsFacilitatedMessagesAsync(id, ct)
                ? (await teamMessageOptionsProvider.GetOptionsAsync(viewer.Id, ct))
                    .OrderBy(t => t.TeamName, StringComparer.OrdinalIgnoreCase)
                    .ToList()
                : [];

        var viewModel = new ProfileViewModel
        {
            Id = profile.Id,
            UserId = id,
            DisplayName = profileInfo.BurnerName,
            IsOwnProfile = isOwnProfile,
            IsApproved = profile.IsApproved,
            NoShowHistory = noShowContext.History,
            CanViewShiftSignups = noShowContext.CanView,
            OnsiteSince = canViewOnsiteChip
                ? await ResolveOnsiteSinceAsync(profileInfo)
                : null,
            CanViewOnsiteChip = canViewOnsiteChip,
            CanViewSentMessages = canViewSentMessages,
            TeamMessageOptions = teamMessageOptions,
        };

        // Index.cshtml is shared with ProfileController.Me; both render the same page shape.
        return View("~/Views/Profile/Index.cshtml", viewModel);
    }

    private async Task<(bool CanView, List<NoShowHistoryItem>? History)> BuildNoShowHistoryContextAsync(
        Guid profileUserId,
        Guid viewerId,
        bool isOwnProfile,
        CancellationToken ct)
    {
        if (isOwnProfile)
        {
            return (false, null);
        }

        var viewerIsCoordinator = (await shiftMgmt.GetCoordinatorTeamIdsAsync(viewerId)).Count > 0;
        var viewerCanViewShiftHistory = viewerIsCoordinator || ShiftRoleChecks.IsPrivilegedSignupApprover(User);
        if (!viewerCanViewShiftHistory)
        {
            return (false, null);
        }

        var noShows = await shiftSignupService.GetNoShowHistoryAsync(profileUserId);
        if (noShows.Count == 0)
        {
            return (true, null);
        }

        var noShowTeamIds = noShows.Select(s => s.TeamId).Distinct().ToList();
        var teamsById = await teamService.GetTeamsAsync(ct);
        var noShowTeamNames = noShowTeamIds
            .Where(teamsById.ContainsKey)
            .ToDictionary(id => id, id => teamsById[id].Name);

        var reviewerIds = noShows
            .Where(s => s.ReviewedByUserId.HasValue)
            .Select(s => s.ReviewedByUserId!.Value)
            .Distinct()
            .ToList();
        var reviewers = reviewerIds.Count == 0
            ? new Dictionary<Guid, UserInfo>()
            : await _userService.GetUserInfosAsync(reviewerIds, ct);

        return (true, noShows.Select(s =>
        {
            var signupTz = DateTimeZoneProviders.Tzdb[s.TimeZoneId];
            var zoned = s.ShiftStart.InZone(signupTz);
            var reviewer = s.ReviewedByUserId.HasValue
                ? reviewers.GetValueOrDefault(s.ReviewedByUserId.Value)
                : null;
            return new NoShowHistoryItem
            {
                ShiftLabel = s.ShiftLabel,
                DepartmentName = noShowTeamNames.GetValueOrDefault(s.TeamId, ""),
                ShiftDateLabel = zoned.ToDateTimeUnspecified().ToMonthDayTime(),
                MarkedByName = reviewer?.BurnerName,
                MarkedAtLabel = s.ReviewedAt?.InZone(signupTz).ToDateTimeUnspecified().ToMonthDayTime()
            };
        }).ToList());
    }

    /// <summary>
    /// Whether the viewer may see the in-platform messages sent to the profile user: a
    /// coordinator or a privileged shift-management role, never on one's own profile. Uses
    /// the same coordinator check as <see cref="BuildNoShowHistoryContextAsync"/> so the two
    /// panels appear under consistent access rules. The rows themselves come from
    /// <c>&lt;vc:audit-log&gt;</c> in the view.
    /// </summary>
    private async Task<bool> CanViewSentMessagesAsync(Guid viewerId, bool isOwnProfile)
    {
        if (isOwnProfile)
            return false;

        var viewerIsCoordinator = (await shiftMgmt.GetCoordinatorTeamIdsAsync(viewerId)).Count > 0;
        var isPrivilegedApprover = (await authorizationService.AuthorizeAsync(User, PolicyNames.PrivilegedSignupApprover)).Succeeded;
        return viewerIsCoordinator || isPrivilegedApprover;
    }

    [HttpGet("{id:guid}/Popover")]
    public async Task<IActionResult> Popover(Guid id, CancellationToken ct)
    {
        var info = await _userService.GetUserInfoAsync(id, ct);
        if (info is null) return NotFound();
        id = info.Id; // memberships and camp below are keyed by the live id

        var profile = info.Profile;
        if (profile is null)
        {
            return PartialView("_HumanPopover",
                ProfileSummaryViewModelBuilder.BuildWithoutProfile(info));
        }

        var memberships = (await teamService.GetTeamsAsync(ct)).Values
            .Where(t => t.IsActive && t.SystemTeamType != SystemTeamType.Volunteers)
            .Select(t => new { TeamInfo = t, Membership = t.Members.FirstOrDefault(m => m.UserId == id) })
            .Where(x => x.Membership is not null)
            .Select(x => new TeamMembership(x.TeamInfo.Name, x.Membership!.Role) { IsHidden = x.TeamInfo.IsHidden })
            .ToList();
        // Camp + roles for the active season; rendered admin-only in the view.
        var camp = await campService.GetCampUserInfoAsync(id, ct);
        var vm = ProfileSummaryViewModelBuilder.BuildWithProfile(info, memberships, camp);

        return PartialView("_HumanPopover", vm);
    }

    // Reduced popover served to anonymous viewers on public team pages (#771).
    // Mirrors the AllowAnonymous Profile/Picture endpoint pattern: only renders
    // when the target user is an active coordinator on a team that publishes
    // its coordinators (IsPublicPage && ShowCoordinatorsOnPublicPage). Returns
    // 404 otherwise so anonymous probes can't enumerate users. Filtering on the
    // controller per peters-hard-rules.md ("controllers ... responsible for
    // formatting, sorting, filtering") and to avoid expanding ITeamServiceRead
    // surface (memory/architecture/interface-method-additions-are-debt.md);
    // mirrors the inline TeamInfo filter the authenticated Popover uses above.
    [AllowAnonymous]
    [HttpGet("{id:guid}/PublicPopover")]
    public async Task<IActionResult> PublicPopover(Guid id, CancellationToken ct)
    {
        var info = await _userService.GetUserInfoAsync(id, ct);
        if (info is null) return NotFound();
        id = info.Id;

        var roleLabels = (await teamService.GetTeamsAsync(ct)).Values
            .Where(t => t.IsActive
                && !t.IsHidden
                && t.IsPublicPage
                && t.ShowCoordinatorsOnPublicPage
                && t.Members.Any(m => m.UserId == id && m.Role == TeamMemberRole.Coordinator))
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(t => $"Coordinator · {t.Name}")
            .ToList();

        if (roleLabels.Count == 0) return NotFound();

        var vm = new PublicPopoverViewModel
        {
            UserId = info.Id,
            DisplayName = info.BurnerName,
            RoleLabels = roleLabels
        };

        return PartialView("_HumanPopoverPublic", vm);
    }

    [HttpGet("{id:guid}/SendMessage")]
    public async Task<IActionResult> SendMessage(Guid id, Guid? teamId, CancellationToken ct)
    {
        var currentUser = await GetCurrentUserInfoAsync(ct);
        if (currentUser is null)
            return NotFound();

        if (currentUser.Id == id)
            return RedirectToAction(nameof(ViewProfile), new { id });

        var targetInfo = await _userService.GetUserInfoAsync(id, ct);
        if (targetInfo is null)
            return NotFound();
        id = targetInfo.Id;
        if (currentUser.Id == id)
            return RedirectToAction(nameof(ViewProfile), new { id });

        if (!await commPrefService.AcceptsFacilitatedMessagesAsync(id, ct))
        {
            SetError("This human has opted out of receiving messages.");
            return RedirectToAction(nameof(ViewProfile), new { id });
        }

        var teamSender = teamId is null
            ? null
            : (await teamMessageOptionsProvider.GetOptionsAsync(currentUser.Id, ct))
                .FirstOrDefault(t => t.TeamId == teamId.Value);
        if (teamId is not null && teamSender is null)
            return Forbid();

        var viewModel = new SendMessageViewModel
        {
            RecipientId = id,
            RecipientDisplayName = targetInfo.BurnerName,
            SenderEmail = currentUser.Email ?? string.Empty,
            SendAsTeamId = teamSender?.TeamId,
            SendAsTeamName = teamSender?.TeamName,
            TeamReplyToEmail = teamSender?.GoogleGroupEmail,
        };

        return View(viewModel);
    }

    [HttpPost("{id:guid}/SendMessage")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SendMessage(Guid id, SendMessageViewModel model, CancellationToken ct)
    {
        var currentUser = await GetCurrentUserInfoAsync(ct);
        if (currentUser is null)
            return NotFound();

        if (currentUser.Id == id)
            return RedirectToAction(nameof(ViewProfile), new { id });

        // Bulk-fetch via section service, not cross-domain nav.
        var participants = await _userService.GetUserInfosAsync([id, currentUser.Id], ct);
        if (!participants.TryGetValue(id, out var targetUser))
            return NotFound();
        id = targetUser.Id;
        if (currentUser.Id == id)
            return RedirectToAction(nameof(ViewProfile), new { id });

        if (!await commPrefService.AcceptsFacilitatedMessagesAsync(id, ct))
        {
            SetError("This human has opted out of receiving messages.");
            return RedirectToAction(nameof(ViewProfile), new { id });
        }

        model.RecipientId = id;
        model.RecipientDisplayName = targetUser.BurnerName;
        model.SenderEmail = currentUser.Email ?? string.Empty;

        var teamSender = model.SendAsTeamId is null
            ? null
            : (await teamMessageOptionsProvider.GetOptionsAsync(currentUser.Id, ct))
                .FirstOrDefault(t => t.TeamId == model.SendAsTeamId.Value);
        if (model.SendAsTeamId is not null && teamSender is null)
            return Forbid();

        model.SendAsTeamName = teamSender?.TeamName;
        model.TeamReplyToEmail = teamSender?.GoogleGroupEmail;

        if (!ModelState.IsValid)
            return View(model);

        if (!participants.TryGetValue(currentUser.Id, out var sender))
            return NotFound();

        var request = FacilitatedMessageRequestBuilder.TryBuild(sender, targetUser, model);
        if (request is null)
        {
            ModelState.AddModelError(string.Empty, sharedLocalizer["Common_Error"].Value);
            return View(model);
        }

        var message = WithTeamReplyTo(emailMessages.FacilitatedMessage(
            request.RecipientEmail,
            request.RecipientDisplayName,
            request.SenderDisplayName,
            request.CleanMessage,
            request.IncludeContactInfo,
            request.SenderEmail,
            request.RecipientPreferredLanguage), teamSender);

        await emailService.SendAsync(message, CancellationToken.None);

        await auditLogService.LogAsync(
            AuditAction.FacilitatedMessageSent,
            nameof(User), targetUser.Id,
            DescribeFacilitatedMessage(targetUser.BurnerName, model.IncludeContactInfo, teamSender),
            currentUser.Id,
            relatedEntityId: teamSender?.TeamId,
            relatedEntityType: teamSender is null ? null : "Team");

        SetSuccess(string.Format(
            localizer["SendMessage_Success"].Value,
            targetUser.BurnerName));

        return RedirectToAction(nameof(ViewProfile), new { id });
    }

    private static EmailMessage WithTeamReplyTo(EmailMessage message, TeamMessageOption? teamSender) =>
        teamSender is null ? message : message with { ReplyTo = teamSender.GoogleGroupEmail };

    private static string DescribeFacilitatedMessage(
        string recipientName,
        bool includeContactInfo,
        TeamMessageOption? teamSender)
    {
        var contactInfo = includeContactInfo ? "yes" : "no";
        return teamSender is null
            ? $"Message sent to {recipientName} (contact info shared: {contactInfo})"
            : $"Message sent to {recipientName} from team {teamSender.TeamName} (contact info shared: {contactInfo})";
    }

    // ─── Search ──────────────────────────────────────────────────────

    [HttpGet("Search")]
    public async Task<IActionResult> Search(string? q, CancellationToken ct)
    {
        var viewModel = new HumanSearchViewModel { Query = q };

        if (!q.HasSearchTerm())
        {
            return View(viewModel);
        }

        // PublicAll = name + bio + public ContactFields. Admin bit gated by code review.
        // Uncapped: return the full match set so relevance ranking surfaces the right person
        // (a hard cap returned an arbitrary subset before sorting). Cheap at our small scale.
        var results = await _userService.SearchUsersAsync(
            q, PersonSearchFields.PublicAll, limit: int.MaxValue, ct);

        // Display sort at controller — memory/architecture/display-sort-in-controllers.md.
        viewModel.Results = results
            .OrderByRelevance()
            .ToList();

        return View(viewModel);
    }


    /// <summary>
    /// The user's "onsite since" instant for the active event year, or null if they are
    /// not yet checked in (or there is no active event). Reads from the cached
    /// <see cref="UserInfo"/> snapshot — no extra DB hit. Same helper as
    /// <c>ProfileController.Me</c> uses for the own-profile chip. Issue
    /// nobodies-collective/Humans#736.
    /// </summary>
    private async Task<Instant?> ResolveOnsiteSinceAsync(UserInfo info)
    {
        var active = await burnSettings.GetActiveAsync();
        if (active is null || active.Year == 0) return null;
        return info.OnsiteSinceForYear(active.Year);
    }
}
