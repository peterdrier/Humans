using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Authorization;
using Humans.Base.Constants;
using Humans.Surveys.Authorization;
using Humans.Surveys.Contracts;
using Humans.Surveys.Domain;
using Humans.Surveys.Services;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Surveys.Tests.Authorization;

/// <summary>
/// Unit tests for <see cref="SurveyAuthorizationHandler"/> — the self-service approval gate's
/// per-survey ownership check (Workgroups design §11). Direct GET/POST-by-id denial for a
/// non-owner, non-Board/Admin Human is the load-bearing case here.
/// </summary>
public sealed class SurveyAuthorizationHandlerTests
{
    private readonly SurveyAuthorizationHandler _handler = new();

    private static readonly Guid AuthorId = Guid.NewGuid();
    private static readonly Guid OtherUserId = Guid.NewGuid();

    // ── Edit ──────────────────────────────────────────────────────────────

    [HumansFact]
    public async Task Author_can_edit_their_own_draft()
    {
        var result = await EvaluateAsync(CreateUser(AuthorId), Detail(SurveyStatus.Draft, AuthorId), SurveyOperation.Edit);
        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task Author_cannot_edit_their_own_survey_once_pending_approval()
    {
        var result = await EvaluateAsync(CreateUser(AuthorId), Detail(SurveyStatus.PendingApproval, AuthorId), SurveyOperation.Edit);
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task NonOwner_cannot_edit_someone_elses_draft()
    {
        var result = await EvaluateAsync(CreateUser(OtherUserId), Detail(SurveyStatus.Draft, AuthorId), SurveyOperation.Edit);
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task BoardOrAdmin_can_edit_any_survey_regardless_of_status()
    {
        var result = await EvaluateAsync(
            CreateUserWithRole(RoleNames.Board), Detail(SurveyStatus.PendingApproval, AuthorId), SurveyOperation.Edit);
        result.Should().BeTrue();
    }

    // ── Submit ────────────────────────────────────────────────────────────

    [HumansFact]
    public async Task Author_can_submit_their_own_draft()
    {
        var result = await EvaluateAsync(CreateUser(AuthorId), Detail(SurveyStatus.Draft, AuthorId), SurveyOperation.Submit);
        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task NonOwner_cannot_submit_someone_elses_draft()
    {
        var result = await EvaluateAsync(CreateUser(OtherUserId), Detail(SurveyStatus.Draft, AuthorId), SurveyOperation.Submit);
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task BoardOrAdmin_cannot_submit_someone_elses_draft()
    {
        // Submit is author-only, regardless of any Board/Admin role also held (design §11).
        var result = await EvaluateAsync(
            CreateUserWithRole(RoleNames.Admin), Detail(SurveyStatus.Draft, AuthorId), SurveyOperation.Submit);
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task Author_cannot_submit_a_survey_already_pending_approval()
    {
        var result = await EvaluateAsync(CreateUser(AuthorId), Detail(SurveyStatus.PendingApproval, AuthorId), SurveyOperation.Submit);
        result.Should().BeFalse();
    }

    // ── ViewResults ───────────────────────────────────────────────────────

    [HumansFact]
    public async Task Author_can_view_their_own_results_once_closed()
    {
        var result = await EvaluateAsync(CreateUser(AuthorId), Detail(SurveyStatus.Closed, AuthorId), SurveyOperation.ViewResults);
        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task Author_cannot_view_their_own_results_before_closing()
    {
        var result = await EvaluateAsync(CreateUser(AuthorId), Detail(SurveyStatus.Open, AuthorId), SurveyOperation.ViewResults);
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task NonOwner_cannot_view_someone_elses_closed_results()
    {
        var result = await EvaluateAsync(CreateUser(OtherUserId), Detail(SurveyStatus.Closed, AuthorId), SurveyOperation.ViewResults);
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task BoardOrAdmin_can_view_results_for_any_survey_at_any_status()
    {
        var result = await EvaluateAsync(
            CreateUserWithRole(RoleNames.Admin), Detail(SurveyStatus.Open, AuthorId), SurveyOperation.ViewResults);
        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task Unauthenticated_user_is_denied_every_operation()
    {
        var unauthenticated = new ClaimsPrincipal(new ClaimsIdentity());
        var result = await EvaluateAsync(unauthenticated, Detail(SurveyStatus.Draft, AuthorId), SurveyOperation.Edit);
        result.Should().BeFalse();
    }

    // ── Helpers ───────────────────────────────────────────────────────────

    private async Task<bool> EvaluateAsync(ClaimsPrincipal user, SurveyDetail resource, SurveyOperation op)
    {
        var requirement = new SurveyOperationRequirement(op);
        var context = new AuthorizationHandlerContext([requirement], user, resource);
        await _handler.HandleAsync(context);
        return context.HasSucceeded;
    }

    private static SurveyDetail Detail(SurveyStatus status, Guid createdByUserId) =>
        new(Guid.NewGuid(), status, EmptyEditable(), createdByUserId);

    private static SurveyEditInput EmptyEditable() =>
        new(
            LocalizedText.Empty, LocalizedText.Empty, LocalizedText.Empty,
            LocalizedText.Empty, LocalizedText.Empty,
            "en", false, null, null, null, null, null, null, []);

    private static ClaimsPrincipal CreateUser(Guid userId) =>
        new(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, userId.ToString())], "test"));

    private static ClaimsPrincipal CreateUserWithRole(string role) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, Guid.NewGuid().ToString()), new Claim(ClaimTypes.Role, role)],
            "test"));
}
