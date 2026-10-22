using Humans.Base.Authorization;
using Humans.Base.Interfaces;
using Humans.Expenses.Authorization;
using Microsoft.AspNetCore.Authorization;

namespace Humans.Expenses;

internal sealed class SectionPolicies : ISectionPolicies
{
    public void AddPolicies(AuthorizationOptions options)
    {
        // Registered here so Backdoor reaches the same View check ExpensesController uses without the requirement type
        // leaving this section.
        options.AddPolicy(PolicyNames.ExpenseReportView, policy =>
            policy.AddRequirements(new ExpenseReportOperationRequirement(ExpenseReportOperation.View)));
    }
}
