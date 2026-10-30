namespace Humans.Finance.Contracts;

/// <summary>Finance's authorization policies, registered by its <c>Section</c>. Read admits Board; Manage never does.</summary>
public static class FinancePolicies
{
    /// <summary>Finance Admin, Board or Admin: Finance, Budget admin, Holded and the expense review queue.</summary>
    public const string Read = "FinanceRead";

    /// <summary>Finance Admin or Admin: every Finance, Budget, Holded and expense decision.</summary>
    public const string Manage = "FinanceManage";
}
