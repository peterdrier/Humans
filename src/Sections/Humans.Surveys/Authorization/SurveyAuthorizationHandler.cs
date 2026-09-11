using System.Security.Claims;
using Humans.Base.Authorization;
using Humans.Surveys.Contracts;
using Humans.Surveys.Services;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Surveys.Authorization;

/// <summary>
/// Resource-based authorization handler for survey operations (Workgroups design §11: Surveys is
/// generically self-service with an approval gate). The rule itself is also enforced in
/// <see cref="SurveyService"/> (state-machine + ownership checks) — this handler is the browser's
/// copy, used to shape the page and answer 403 before the service would answer with an exception.
/// </summary>
internal sealed class SurveyAuthorizationHandler : AuthorizationHandler<SurveyOperationRequirement, SurveyDetail>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        SurveyOperationRequirement requirement,
        SurveyDetail resource)
    {
        if (!Guid.TryParse(context.User.FindFirstValue(ClaimTypes.NameIdentifier), out var userId))
            return Task.CompletedTask;

        var isBoardOrAdmin = RoleChecks.IsAdminOrBoard(context.User);
        var isOwner = resource.CreatedByUserId == userId;

        var allowed = requirement.Operation switch
        {
            SurveyOperation.Edit => isBoardOrAdmin || (isOwner && resource.Status == SurveyStatus.Draft),
            SurveyOperation.Submit => isOwner && resource.Status == SurveyStatus.Draft,
            SurveyOperation.ViewResults => isBoardOrAdmin || (isOwner && resource.Status == SurveyStatus.Closed),
            _ => false,
        };

        if (allowed)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
