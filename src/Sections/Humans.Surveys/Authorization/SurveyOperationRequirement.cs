using Microsoft.AspNetCore.Authorization;

namespace Humans.Surveys.Authorization;

/// <summary>Operations that can be performed on a specific survey (self-service authoring gate).</summary>
internal enum SurveyOperation
{
    /// <summary>Board/Admin may edit any survey; the author may edit only their own while Draft.</summary>
    Edit,

    /// <summary>Draft → PendingApproval. Author-only, regardless of any Board/Admin role they also hold.</summary>
    Submit,

    /// <summary>Board/Admin see every survey's results; the author sees their own only after it closes.</summary>
    ViewResults,
}

/// <summary>
/// Resource-based authorization requirement for survey operations.
/// Used with <c>IAuthorizationService.AuthorizeAsync(User, surveyDetail, requirement)</c> where the
/// resource is a <see cref="Humans.Surveys.Services.SurveyDetail"/>.
/// </summary>
internal sealed class SurveyOperationRequirement(SurveyOperation operation) : IAuthorizationRequirement
{
    public SurveyOperation Operation { get; } = operation;
}
