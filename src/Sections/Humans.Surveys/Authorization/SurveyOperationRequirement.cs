using Microsoft.AspNetCore.Authorization;

namespace Humans.Surveys.Authorization;

internal enum SurveyOperation
{
    /// <summary>Surveys Admin/Admin may edit any survey; the author may edit only their own while Draft.</summary>
    Edit,

    /// <summary>Draft → PendingApproval. Author-only, regardless of any role they also hold.</summary>
    Submit,

    /// <summary>Surveys Admin, Board and Admin see every survey's results; the author sees their own only after it closes.</summary>
    ViewResults,

    /// <summary>The CSV and JSON downloads: Surveys Admin/Admin for any survey, the author for their own once closed.</summary>
    ExportResults,

    /// <summary>
    /// Rehearsal, including sending the invitation to yourself. Surveys Admin, Board and Admin may rehearse any survey;
    /// anyone else only one they authored.
    /// </summary>
    Preview,
}

internal sealed class SurveyOperationRequirement(SurveyOperation operation) : IAuthorizationRequirement
{
    public SurveyOperation Operation { get; } = operation;
}
