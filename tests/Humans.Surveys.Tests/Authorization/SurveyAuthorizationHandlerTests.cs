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
/// non-owner without a survey-wide role is the load-bearing case here.
/// </summary>
public sealed class SurveyAuthorizationHandlerTests
{
    public enum OwnershipOperation
    {
        Edit,
        Submit,
        ViewResults,
        Preview
    }

    public enum OwnershipAccount
    {
        Merged,
        Missing,
        Cancelled,
        Stranger
    }

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
    [Xunit.InlineData(OwnershipOperation.Edit, OwnershipAccount.Merged)]
    [Xunit.InlineData(OwnershipOperation.Edit, OwnershipAccount.Missing)]
    [Xunit.InlineData(OwnershipOperation.Edit, OwnershipAccount.Cancelled)]
    [Xunit.InlineData(OwnershipOperation.Edit, OwnershipAccount.Stranger)]
    [Xunit.InlineData(OwnershipOperation.Submit, OwnershipAccount.Merged)]
    [Xunit.InlineData(OwnershipOperation.Submit, OwnershipAccount.Missing)]
    [Xunit.InlineData(OwnershipOperation.Submit, OwnershipAccount.Cancelled)]
    [Xunit.InlineData(OwnershipOperation.Submit, OwnershipAccount.Stranger)]
    [Xunit.InlineData(OwnershipOperation.ViewResults, OwnershipAccount.Merged)]
    [Xunit.InlineData(OwnershipOperation.ViewResults, OwnershipAccount.Missing)]
    [Xunit.InlineData(OwnershipOperation.ViewResults, OwnershipAccount.Cancelled)]
    [Xunit.InlineData(OwnershipOperation.ViewResults, OwnershipAccount.Stranger)]
    [Xunit.InlineData(OwnershipOperation.Preview, OwnershipAccount.Merged)]
    [Xunit.InlineData(OwnershipOperation.Preview, OwnershipAccount.Missing)]
    [Xunit.InlineData(OwnershipOperation.Preview, OwnershipAccount.Cancelled)]
    [Xunit.InlineData(OwnershipOperation.Preview, OwnershipAccount.Stranger)]
    public async Task Ownership_ResolvesTheAccountWithoutWideningAccess(OwnershipOperation operation, OwnershipAccount account)
    {
        using var request = new CancellationTokenSource();
        var cookieId = Guid.NewGuid();
        var survivorId = Guid.NewGuid();
        var users = Substitute.For<IUserService>();
        users.GetUserInfoAsync(cookieId, Arg.Any<CancellationToken>()).Returns(call =>
        {
            call.Arg<CancellationToken>().ThrowIfCancellationRequested();
            return new ValueTask<UserInfo?>(account == OwnershipAccount.Missing
                ? null : UserInfo.Create(new User { Id = survivorId }, [], [], null));
        });
        using var services = new ServiceCollection().AddSingleton(users)
            .AddSingleton<IHttpContextAccessor>(new HttpContextAccessor
            { HttpContext = new DefaultHttpContext { RequestAborted = request.Token } }).BuildServiceProvider();
        var handler = ActivatorUtilities.CreateInstance<SurveyAuthorizationHandler>(services);
        var op = operation switch
        {
            OwnershipOperation.Edit => SurveyOperation.Edit,
            OwnershipOperation.Submit => SurveyOperation.Submit,
            OwnershipOperation.ViewResults => SurveyOperation.ViewResults,
            OwnershipOperation.Preview => SurveyOperation.Preview,
            _ => throw new ArgumentOutOfRangeException(nameof(operation))
        };
        var status = op == SurveyOperation.ViewResults ? SurveyStatus.Closed : SurveyStatus.Draft;
        var ownerId = account == OwnershipAccount.Stranger ? Guid.NewGuid() : survivorId;
        var requirement = new SurveyOperationRequirement(op);
        var user = account == OwnershipAccount.Missing
            ? new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, cookieId.ToString()),
                new Claim(ClaimTypes.Role, RoleNames.Admin)], "test")) : CreateUser(cookieId);
        var context = new AuthorizationHandlerContext([requirement], user, Detail(status, ownerId));
        if (account == OwnershipAccount.Cancelled)
        {
            await request.CancelAsync();
            Func<Task> run = () => handler.HandleAsync(context);
            await run.Should().ThrowAsync<OperationCanceledException>();
        }
        else
        {
            await handler.HandleAsync(context);
            context.HasSucceeded.Should().Be(account == OwnershipAccount.Merged);
        }
    }

    [HumansFact]
    public async Task Edit_AuthorOwnDraft_IsAllowed()
    {
        var result = await EvaluateAsync(CreateUser(AuthorId), Detail(SurveyStatus.Draft, AuthorId), SurveyOperation.Edit);
        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task Edit_AuthorPendingApproval_IsDenied()
    {
        var result = await EvaluateAsync(CreateUser(AuthorId), Detail(SurveyStatus.PendingApproval, AuthorId), SurveyOperation.Edit);
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task Edit_NonOwner_IsDenied()
    {
        var result = await EvaluateAsync(CreateUser(OtherUserId), Detail(SurveyStatus.Draft, AuthorId), SurveyOperation.Edit);
        result.Should().BeFalse();
    }

    [HumansTheory]
    [Xunit.InlineData(SurveysRoles.SurveysAdmin, true)]
    [Xunit.InlineData(RoleNames.Admin, true)]
    [Xunit.InlineData(RoleNames.Board, false)]
    public async Task Edit_AnyStatus_NeedsSurveysAdmin(string role, bool expected)
    {
        var result = await EvaluateAsync(
            CreateUserWithRole(role), Detail(SurveyStatus.PendingApproval, AuthorId), SurveyOperation.Edit);
        result.Should().Be(expected);
    }

    [HumansFact]
    public async Task Submit_AuthorOwnDraft_IsAllowed()
    {
        var result = await EvaluateAsync(CreateUser(AuthorId), Detail(SurveyStatus.Draft, AuthorId), SurveyOperation.Submit);
        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task Submit_NonOwner_IsDenied()
    {
        var result = await EvaluateAsync(CreateUser(OtherUserId), Detail(SurveyStatus.Draft, AuthorId), SurveyOperation.Submit);
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task Submit_AdminOnOthersDraft_IsDenied()
    {
        // Submit is author-only, regardless of any role also held.
        var result = await EvaluateAsync(
            CreateUserWithRole(RoleNames.Admin), Detail(SurveyStatus.Draft, AuthorId), SurveyOperation.Submit);
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task Submit_AuthorPendingApproval_IsDenied()
    {
        var result = await EvaluateAsync(CreateUser(AuthorId), Detail(SurveyStatus.PendingApproval, AuthorId), SurveyOperation.Submit);
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task ViewResults_AuthorClosedSurvey_IsAllowed()
    {
        var result = await EvaluateAsync(CreateUser(AuthorId), Detail(SurveyStatus.Closed, AuthorId), SurveyOperation.ViewResults);
        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task ViewResults_AuthorBeforeClose_IsDenied()
    {
        var result = await EvaluateAsync(CreateUser(AuthorId), Detail(SurveyStatus.Open, AuthorId), SurveyOperation.ViewResults);
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task ViewResults_NonOwnerClosedSurvey_IsDenied()
    {
        var result = await EvaluateAsync(CreateUser(OtherUserId), Detail(SurveyStatus.Closed, AuthorId), SurveyOperation.ViewResults);
        result.Should().BeFalse();
    }

    [HumansTheory]
    [Xunit.InlineData(SurveysRoles.SurveysAdmin)]
    [Xunit.InlineData(RoleNames.Admin)]
    [Xunit.InlineData(RoleNames.Board)]
    public async Task ViewResults_ReaderAnyStatus_IsAllowed(string role)
    {
        var result = await EvaluateAsync(
            CreateUserWithRole(role), Detail(SurveyStatus.Open, AuthorId), SurveyOperation.ViewResults);
        result.Should().BeTrue();
    }

    [HumansTheory]
    [Xunit.InlineData(SurveysRoles.SurveysAdmin, true)]
    [Xunit.InlineData(RoleNames.Admin, true)]
    [Xunit.InlineData(RoleNames.Board, false)]
    public async Task Download_OthersResults_NeedsSurveysAdmin(string role, bool expected)
    {
        var result = await EvaluateAsync(
            CreateUserWithRole(role), Detail(SurveyStatus.Closed, AuthorId), SurveyOperation.ExportResults);
        result.Should().Be(expected);
    }

    [HumansTheory]
    [Xunit.InlineData(SurveyStatus.Closed, true)]
    [Xunit.InlineData(SurveyStatus.Open, false)]
    public async Task Download_AuthorClosedSurvey_IsAllowed(SurveyStatus status, bool expected)
    {
        var result = await EvaluateAsync(CreateUser(AuthorId), Detail(status, AuthorId), SurveyOperation.ExportResults);
        result.Should().Be(expected);
    }

    [HumansFact]
    public async Task Preview_AuthorAnyStatus_IsAllowed()
    {
        var result = await EvaluateAsync(CreateUser(AuthorId), Detail(SurveyStatus.PendingApproval, AuthorId), SurveyOperation.Preview);
        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task Preview_NonOwner_IsDenied()
    {
        var result = await EvaluateAsync(CreateUser(OtherUserId), Detail(SurveyStatus.Draft, AuthorId), SurveyOperation.Preview);
        result.Should().BeFalse();
    }

    [HumansFact]
    public async Task Preview_BoardAnySurvey_IsAllowed()
    {
        var result = await EvaluateAsync(
            CreateUserWithRole(RoleNames.Board), Detail(SurveyStatus.Draft, AuthorId), SurveyOperation.Preview);
        result.Should().BeTrue();
    }

    [HumansFact]
    public async Task Operations_Unauthenticated_AreDenied()
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
