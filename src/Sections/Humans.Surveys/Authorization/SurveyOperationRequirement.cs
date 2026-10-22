using Microsoft.AspNetCore.Authorization;

namespace Humans.Surveys.Authorization;

internal enum SurveyOperation
{
    /// <summary>Board/Admin may edit any survey; the author may edit only their own while Draft.</summary>
    Edit,

    /// <summary>Draft → PendingApproval. Author-only, regardless of any Board/Admin role they also hold.</summary>
    Submit,

    /// <summary>Board/Admin see every survey's results; the author sees their own only after it closes.</summary>
    ViewResults,

    /// <summary>
    /// Rehearsal, including sending the invitation to yourself. Board/Admin may rehearse any survey; anyone else only
    /// one they authored.
    /// </summary>
    Preview,
}

internal sealed class SurveyOperationRequirement(SurveyOperation operation) : IAuthorizationRequirement
{
    public SurveyOperation Operation { get; } = operation;
}
