using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Testing;
using Humans.Workgroups.Controllers;
using Humans.Workgroups.Domain;
using Humans.Workgroups.Tests.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using NSubstitute;

namespace Humans.Workgroups.Tests.Controllers;

/// <summary>
/// The deny paths design §5 requires of the member pages: a non-member reaching a
/// member-only mutation route gets Forbid, while Join, Leave, RequestStatus and commenting
/// stay open to any signed-in human. Every member-only POST goes through one private helper
/// (<c>ActAsync</c>), so a representative sample of routes covers the guard.
/// </summary>
public sealed class WorkgroupsControllerAuthorizationTests : WorkgroupsTestHarness
{
    [HumansFact]
    public async Task NonMember_MarkingTheGroupDone_IsForbidden()
    {
        var (controller, workgroup) = await BuildAsync(asMember: false);

        var result = await controller.Done(workgroup.Slug, WorkgroupDormantReason.Delivered, Ct);

        result.Should().BeOfType<ForbidResult>();
        (await OpenContext().Workgroups.FindAsync([workgroup.Id], Ct))!
            .Status.Should().Be(WorkgroupStatus.Active, "the mutation must not have run");
    }

    [HumansFact]
    public async Task NonMember_DeletingAMeeting_IsForbidden()
    {
        var (controller, workgroup) = await BuildAsync(asMember: false);
        var meeting = await AddMeetingAsync(workgroup.Id, Clock.GetCurrentInstant());

        var result = await controller.DeleteMeeting(workgroup.Slug, meeting.Id, Ct);

        result.Should().BeOfType<ForbidResult>();
        (await OpenContext().Meetings.FindAsync([meeting.Id], Ct))!
            .DeletedAt.Should().BeNull();
    }

    [HumansFact]
    public async Task NonMember_PublishingADocument_IsForbidden()
    {
        var (controller, workgroup) = await BuildAsync(asMember: false);
        var document = await AddDocumentAsync(workgroup.Id);

        var result = await controller.PublishDocument(workgroup.Slug, document.Id, Ct);

        result.Should().BeOfType<ForbidResult>();
        (await OpenContext().Documents.FindAsync([document.Id], Ct))!
            .Status.Should().Be(WorkgroupDocumentStatus.Draft);
    }

    [HumansFact]
    public async Task NonMember_HidingAComment_IsForbidden()
    {
        var (controller, workgroup) = await BuildAsync(asMember: false);
        var document = await AddDocumentAsync(workgroup.Id);
        var comment = await AddCommentAsync(document.Id);

        var result = await controller.HideComment(workgroup.Slug, comment.Id, document.Id, "spam", Ct);

        result.Should().BeOfType<ForbidResult>();
        (await OpenContext().Comments.FindAsync([comment.Id], Ct))!
            .HiddenAt.Should().BeNull();
    }

    [HumansFact]
    public async Task NonMember_SettingCoordinators_IsForbidden()
    {
        var (controller, workgroup) = await BuildAsync(asMember: false);

        var result = await controller.Coordinators(workgroup.Slug, [Guid.NewGuid()], Ct);

        result.Should().BeOfType<ForbidResult>();
    }

    [HumansFact]
    public async Task Member_MarkingTheGroupDone_IsAllowed()
    {
        var (controller, workgroup) = await BuildAsync(asMember: true);

        var result = await controller.Done(workgroup.Slug, WorkgroupDormantReason.Delivered, Ct);

        result.Should().BeOfType<RedirectToActionResult>();
    }

    [HumansFact]
    public async Task Board_ActingOnAGroupTheyAreNotIn_IsAllowed()
    {
        var (controller, workgroup) = await BuildAsync(asMember: false, isBoard: true);

        var result = await controller.Done(workgroup.Slug, WorkgroupDormantReason.Delivered, Ct);

        result.Should().BeOfType<RedirectToActionResult>();
    }

    [HumansFact]
    public async Task NonMember_Joining_IsAllowed()
    {
        var (controller, workgroup) = await BuildAsync(asMember: false);

        var result = await controller.Join(workgroup.Slug, Ct);

        result.Should().BeOfType<RedirectToActionResult>();
    }

    [HumansFact]
    public async Task NonMember_RequestingAStatusUpdate_IsAllowed()
    {
        var (controller, workgroup) = await BuildAsync(asMember: false);

        var result = await controller.RequestStatus(workgroup.Slug, "Where is the report?", Ct);

        result.Should().BeOfType<RedirectToActionResult>();
    }

    [HumansFact]
    public async Task NonMember_CommentingWhileTheWindowIsOpen_IsAllowed()
    {
        var (controller, workgroup) = await BuildAsync(asMember: false);
        var now = Clock.GetCurrentInstant();
        var document = await AddDocumentAsync(
            workgroup.Id,
            status: WorkgroupDocumentStatus.Published,
            categories: ["Scope"],
            opensAt: now - Duration.FromHours(1),
            closesAt: now + Duration.FromHours(1));

        var result = await controller.AddComment(workgroup.Slug, document.Id, "Scope", "A remark", Ct);

        result.Should().BeOfType<RedirectToActionResult>();
        (await OpenContext().Comments.ToListAsync(Ct)).Should().ContainSingle();
    }

    /// <summary>An Active group whose only member is its coordinator, plus a controller for
    /// either that coordinator (<paramref name="asMember"/>) or an outsider.</summary>
    private async Task<(WorkgroupsController Controller, Workgroup Workgroup)> BuildAsync(
        bool asMember, bool isBoard = false)
    {
        var workgroup = await SeedWorkgroupAsync();
        var actorId = asMember
            ? workgroup.Members.Single().UserId
            : SeedUser("Outsider");

        var controller = new WorkgroupsController(
            NewService(), Users, Teams,
            Substitute.For<IStringLocalizer<WorkgroupsResource>>(),
            Clock,
            NullLogger<WorkgroupsController>.Instance);

        var services = new ServiceCollection();
        services.AddLogging();
        var claims = new List<Claim> { new(ClaimTypes.NameIdentifier, actorId.ToString()) };
        if (isBoard) claims.Add(new Claim(ClaimTypes.Role, RoleNames.Board));

        var http = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = new ClaimsPrincipal(new ClaimsIdentity(claims, authenticationType: "test"))
        };

        controller.ControllerContext = new ControllerContext
        {
            HttpContext = http,
            ActionDescriptor = new ControllerActionDescriptor { ActionName = "Test" }
        };
        controller.TempData = new TempDataDictionary(http, Substitute.For<ITempDataProvider>());
        controller.Url = Substitute.For<IUrlHelper>();
        return (controller, workgroup);
    }
}
