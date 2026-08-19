using Humans.Base.Authorization;
using Humans.Base.Interfaces;

namespace Humans.Surveys;

/// <summary>Surveys' contribution to the shared "Messaging" admin group (nobodies-collective/Humans#1077).</summary>
public sealed class AdminNav : ISectionAdminNav
{
    public IEnumerable<AdminNavGroup> Groups() =>
    [
        new("Messaging", [
            // First-party survey tool (own section); Board happens to be its main
            // user today, but it is not Governance.
            new("Surveys", "SurveyAdmin", "Index", null, null, "fa-solid fa-square-poll-vertical", PolicyNames.BoardOrAdmin, Weight: 30)
        ], Weight: 100)
    ];
}
