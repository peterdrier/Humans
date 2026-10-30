using System.Security.Claims;
using Humans.Surveys.Contracts;
using Humans.Surveys.Services;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Surveys.Authorization;

/// <summary>
/// The browser's copy of the rule <see cref="SurveyService"/> also enforces: it shapes the page and answers 403 before
/// the service would throw.
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

        var manages = SurveysRoles.ManagesEverySurvey(context.User);
        var sees = SurveysRoles.SeesEverySurvey(context.User);
        var isOwner = resource.CreatedByUserId == userId;
        var ownClosed = isOwner && resource.Status == SurveyStatus.Closed;

        var allowed = requirement.Operation switch
        {
            SurveyOperation.Edit => manages || (isOwner && resource.Status == SurveyStatus.Draft),
            SurveyOperation.Submit => isOwner && resource.Status == SurveyStatus.Draft,
            SurveyOperation.ViewResults => sees || ownClosed,
            SurveyOperation.ExportResults => manages || ownClosed,
            SurveyOperation.Preview => sees || isOwner,
            _ => false,
        };

        if (allowed)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
