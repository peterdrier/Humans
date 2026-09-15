using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Workgroups.Authorization;
using Humans.Workgroups.Domain;
using Humans.Workgroups.Services;
using Microsoft.AspNetCore.Authorization;
using NodaTime;

namespace Humans.Workgroups.Tests.Authorization;

/// <summary>
/// The deny paths of design §5 and §20: a non-member fails Member, a member fails
/// Administer, a Dormant group fails Member even for a member, Board/Admin passes
/// Administer, and an unauthenticated principal fails Read.
/// </summary>
public sealed class WorkgroupAuthorizationHandlerTests
{
    private readonly WorkgroupAuthorizationHandler _handler = new();
    private static readonly Guid MemberId = Guid.NewGuid();
    private static readonly Guid StrangerId = Guid.NewGuid();

    [HumansTheory]
    [Xunit.InlineData(RoleNames.Board)]
    [Xunit.InlineData(RoleNames.Admin)]
    public async Task Member_BoardAndAdminMayEditOnlyActiveGroups(string role)
    {
        (await EvaluateAsync(SignedInWithRole(role), ActiveWorkgroup(), WorkgroupOperationRequirement.Member))
            .Should().BeTrue();
        (await EvaluateAsync(SignedInWithRole(role), DormantWorkgroup(), WorkgroupOperationRequirement.Member))
            .Should().BeFalse();
    }

    [HumansFact]
    public async Task Read_Anonymous_Fails()
    {
        var result = await EvaluateAsync(Anonymous(), ActiveWorkgroup(), WorkgroupOperationRequirement.Read);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task Read_AnySignedInHuman_Succeeds()
    {
        var result = await EvaluateAsync(SignedInAs(StrangerId), ActiveWorkgroup(), WorkgroupOperationRequirement.Read);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task Member_ANonMember_Fails()
    {
        var result = await EvaluateAsync(
            SignedInAs(StrangerId), ActiveWorkgroup(), WorkgroupOperationRequirement.Member);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task Member_ACurrentMemberOfAnActiveGroup_Succeeds()
    {
        var result = await EvaluateAsync(
            SignedInAs(MemberId), ActiveWorkgroup(), WorkgroupOperationRequirement.Member);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task Member_ACurrentMemberOfADormantGroup_Fails()
    {
        var result = await EvaluateAsync(
            SignedInAs(MemberId), DormantWorkgroup(), WorkgroupOperationRequirement.Member);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task Administer_APlainMember_Fails()
    {
        var result = await EvaluateAsync(
            SignedInAs(MemberId), ActiveWorkgroup(), WorkgroupOperationRequirement.Administer);

        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task Administer_Board_Succeeds()
    {
        var result = await EvaluateAsync(
            SignedInWithRole(RoleNames.Board), ActiveWorkgroup(), WorkgroupOperationRequirement.Administer);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task Administer_Admin_Succeeds()
    {
        var result = await EvaluateAsync(
            SignedInWithRole(RoleNames.Admin), ActiveWorkgroup(), WorkgroupOperationRequirement.Administer);

        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task Administer_OnADormantGroup_StillSucceedsForBoard()
    {
        var result = await EvaluateAsync(
            SignedInWithRole(RoleNames.Board), DormantWorkgroup(), WorkgroupOperationRequirement.Administer);

        result.Should().BeTrue();
    }

    private async Task<bool> EvaluateAsync(
        ClaimsPrincipal user, WorkgroupInfo resource, WorkgroupOperationRequirement requirement)
    {
        var context = new AuthorizationHandlerContext([requirement], user, resource);
        await _handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    private static WorkgroupInfo ActiveWorkgroup() => Workgroup(WorkgroupStatus.Active);

    private static WorkgroupInfo DormantWorkgroup() => Workgroup(WorkgroupStatus.Dormant);

    private static WorkgroupInfo Workgroup(WorkgroupStatus status) => new(
        Guid.NewGuid(), "finance-2026", "Finance 2026", "Purpose", "A report",
        WorkgroupDeliverableKind.Report, WorkgroupAudience.Board, null, status,
        status == WorkgroupStatus.Dormant ? WorkgroupDormantReason.Quiet : null,
        null, null, null, null, Instant.FromUtc(2026, 1, 1, 0, 0),
        Instant.FromUtc(2026, 1, 1, 0, 0), null, null,
        [new WorkgroupMemberInfo(Guid.NewGuid(), MemberId, WorkgroupMemberRole.Coordinator,
            Instant.FromUtc(2026, 1, 1, 0, 0), null)],
        [], [], []);

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    private static ClaimsPrincipal SignedInAs(Guid userId) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "TestAuth"));

    private static ClaimsPrincipal SignedInWithRole(string role) => new(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()),
            new Claim(ClaimTypes.Role, role)
        ], "TestAuth"));
}
