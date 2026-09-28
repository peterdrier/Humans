using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Humans.Expenses.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Expenses;

/// <summary>
/// Expenses' authorization policies, at the project root by convention. Discovered by Shell
/// alongside <see cref="Section"/> — nothing names it.
/// </summary>
internal sealed class SectionPolicies : ISectionPolicies
{
    public void AddPolicies(AuthorizationOptions options)
    {
        // Resource-based (the resource is the ExpenseReportDto). Registered here, the handler's
        // own section, the same way Camps' CampComplianceAccess registers where
        // CampComplianceAccessHandler lives — so Backdoor (peterdrier/Humans#1838) can reach the
        // same View check ExpensesController uses via IAuthorizationService.AuthorizeAsync(User,
        // report, PolicyNames.ExpenseReportView), without ExpenseReportOperationRequirement
        // leaving this section.
        options.AddPolicy(PolicyNames.ExpenseReportView, policy =>
            policy.AddRequirements(new ExpenseReportOperationRequirement(ExpenseReportOperation.View)));
    }
}
