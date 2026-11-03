using System.Security.Claims;
using Humans.Base.Authorization;

namespace Humans.Surveys.Contracts;

/// <summary>Surveys' grantable role and its predicates. Declared to Auth by Surveys' <c>Section</c>.</summary>
public static class SurveysRoles
{
    /// <summary>
    /// Surveys Admin: approves or rejects submitted surveys, edits any survey, opens and closes them, sends invitations
    /// and reads every survey's results. Confers nothing outside Surveys.
    /// </summary>
    public const string SurveysAdmin = "SurveysAdmin";

    /// <summary>Surveys Admin or Admin: changes any survey.</summary>
    public static bool ManagesEverySurvey(ClaimsPrincipal user) => user.IsInRole(SurveysAdmin) || RoleChecks.IsAdmin(user);

    /// <summary>Surveys Admin, Board or Admin: sees every survey and its results.</summary>
    public static bool SeesEverySurvey(ClaimsPrincipal user) => ManagesEverySurvey(user) || RoleChecks.IsBoard(user);
}
