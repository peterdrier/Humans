namespace Humans.Surveys.Contracts;

/// <summary>Surveys' authorization policies, registered by its <c>Section</c>. Read admits Board; Manage never does.</summary>
public static class SurveysPolicies
{
    /// <summary>Surveys Admin, Board or Admin: the approval queue, recipient review and every survey's results.</summary>
    public const string Read = "SurveysRead";

    /// <summary>Surveys Admin or Admin: approve, reject, open, close and send any survey.</summary>
    public const string Manage = "SurveysManage";
}
