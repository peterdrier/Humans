using System.Security.Claims;
using AwesomeAssertions;
using Humans.Base.Constants;
using Humans.Surveys.Authorization;
using Humans.Surveys.Contracts;
using Humans.Surveys.Domain;
using Humans.Surveys.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Humans.Users.Contracts;
using NSubstitute;

namespace Humans.Surveys.Tests.Authorization;

/// <summary>
/// Unit tests for <see cref="SurveyAuthorizationHandler"/> — the self-service approval gate's
/// per-survey ownership check. Direct GET/POST-by-id denial for a
/// non-owner, non-Board/Admin Human is the load-bearing case here.
/// </summary>
public sealed class SurveyAuthorizationHandlerTests
{
    private readonly SurveyAuthorizationHandler _handler;

    public SurveyAuthorizationHandlerTests()
    {
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => UserInfo.Create(new User { Id = call.Arg<Guid>() }, [], [], null));
        _handler = new SurveyAuthorizationHandler(users, new HttpContextAccessor());
    }

    private static readonly Guid AuthorId = Guid.NewGuid();
    private static readonly Guid OtherUserId = Guid.NewGuid();

    [HumansTheory]
    [Xunit.InlineData((int)SurveyOperation.Edit, "merged")]
    [Xunit.InlineData((int)SurveyOperation.Edit, "missing")]
    [Xunit.InlineData((int)SurveyOperation.Edit, "cancelled")]
    [Xunit.InlineData((int)SurveyOperation.Edit, "stranger")]
    [Xunit.InlineData((int)SurveyOperation.Submit, "merged")]
    [Xunit.InlineData((int)SurveyOperation.Submit, "missing")]
    [Xunit.InlineData((int)SurveyOperation.Submit, "cancelled")]
    [Xunit.InlineData((int)SurveyOperation.Submit, "stranger")]
    [Xunit.InlineData((int)SurveyOperation.ViewResults, "merged")]
    [Xunit.InlineData((int)SurveyOperation.ViewResults, "missing")]
    [Xunit.InlineData((int)SurveyOperation.ViewResults, "cancelled")]
    [Xunit.InlineData((int)SurveyOperation.ViewResults, "stranger")]
    [Xunit.InlineData((int)SurveyOperation.Preview, "merged")]
    [Xunit.InlineData((int)SurveyOperation.Preview, "missing")]
    [Xunit.InlineData((int)SurveyOperation.Preview, "cancelled")]
    [Xunit.InlineData((int)SurveyOperation.Preview, "stranger")]
    public async Task Ownership_ResolvesTheAccountWithoutWideningAccess(int operation, string account)
    {
        using var request = new CancellationTokenSource();
        var cookieId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(cookieId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return new ValueTask<UserInfo?>(string.Equals(account, "missing", StringComparison.Ordinal)
                ? null : UserInfo.Create(new User { Id = survivorId }, [], [], null));
        });
        using var services = new ServiceCollection().AddSingleton(users)
            .AddSingleton<IHttpContextAccessor>(new HttpContextAccessor
                { HttpContext = new DefaultHttpContext { RequestAborted = request.Token } }).BuildServiceProvider();
        var handler = ActivatorUtilities.CreateInstance<SurveyAuthorizationHandler>(services);
        var op = (SurveyOperation)operation;
        var status = op == SurveyOperation.ViewResults ? SurveyStatus.Closed : SurveyStatus.Draft;
        var ownerId = string.Equals(account, "stranger", StringComparison.Ordinal) ? Guid.NewGuid() : survivorId;
        var requirement = new SurveyOperationRequirement(op);
        var user = string.Equals(account, "missing", StringComparison.Ordinal)
            ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, cookieId.ToString()),
                new Claim(ClaimTypes.Role, RoleNames.Admin)], "test")) : CreateUser(cookieId);
        var context = new AuthorizationHandlerContext([requirement], user, Detail(status, ownerId));
        if (string.Equals(account, "cancelled", StringComparison.Ordinal))
        {
            await request.CancelAsync();
            Func<Task> run = () => handler.HandleAsync(context);
            await run.Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            await handler.HandleAsync(context);
            context.HasSucceeded.Should().Be(string.Equals(account, "merged", StringComparison.Ordinal));
        }
    }

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
        // Submit is author-only, regardless of any Board/Admin role also held.
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
    public async Task Author_can_preview_their_own_survey_at_any_status()
    {
        var result = await EvaluateAsync(CreateUser(AuthorId), Detail(SurveyStatus.PendingApproval, AuthorId), SurveyOperation.Preview);
        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task NonOwner_cannot_preview_someone_elses_survey()
    {
        var result = await EvaluateAsync(CreateUser(OtherUserId), Detail(SurveyStatus.Draft, AuthorId), SurveyOperation.Preview);
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task BoardOrAdmin_can_preview_any_survey()
    {
        var result = await EvaluateAsync(
            CreateUserWithRole(RoleNames.Board), Detail(SurveyStatus.Draft, AuthorId), SurveyOperation.Preview);
        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task Unauthenticated_user_is_denied_every_operation()
    {
        var unauthenticated = new ClaimsPrincipal(new ClaimsIdentity());
        var result = await EvaluateAsync(unauthenticated, Detail(SurveyStatus.Draft, AuthorId), SurveyOperation.Edit);
        result.Should().BeFalse();
    }

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
