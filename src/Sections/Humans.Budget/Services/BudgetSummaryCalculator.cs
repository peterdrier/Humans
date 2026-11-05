using System.Globalization;
using System.Resources;
using Humans.Budget.Contracts;
using Humans.Finance.Contracts;

namespace Humans.Budget.Services;

internal static class BudgetSummaryCalculator
{
    private static readonly ResourceManager Resources = new(typeof(BudgetResource));

    internal static BudgetSummaryResult ComputeBudgetSummary(IReadOnlyList<BudgetGroupDetail> groups)
    {
        var budgetLineItems = groups
            .SelectMany(g => g.Categories)
            .SelectMany(c => c.LineItems)
            .Where(li => !li.IsCashflowOnly)
            .ToList();

        var vatProjections = budgetLineItems
            .Where(li => li.VatRate > 0 && li.ExpectedDate.HasValue)
            .Select(li => new
            {
                VatAmount = VatCalculation.ComputePortion(li.Amount, li.VatRate),
                IsExpense = li.Amount > 0 // Income generates VAT liability (expense).
            })
            .ToList();

        var income = budgetLineItems.Where(li => li.Amount > 0).Sum(li => li.Amount);
        var expenses = budgetLineItems.Where(li => li.Amount < 0).Sum(li => li.Amount);
        var vatExpenses = vatProjections.Where(v => v.IsExpense).Sum(v => v.VatAmount);
        var vatCredits = vatProjections.Where(v => !v.IsExpense).Sum(v => v.VatAmount);

        var totalIncome = income + vatCredits;
        var totalExpenses = expenses - vatExpenses;
        var netBalance = totalIncome + totalExpenses;

        var incomeCategories = groups
            .SelectMany(g => g.Categories)
            .Select(c => (c.Name, Amount: c.LineItems.Where(li => li.Amount > 0 && !li.IsCashflowOnly).Sum(li => li.Amount)))
            .Where(c => c.Amount > 0)
            .OrderByDescending(c => c.Amount)
            .ToList();

        if (vatCredits > 0)
            incomeCategories.Add((Resources.GetString("Budget_VatCredit", CultureInfo.CurrentUICulture)!, vatCredits));

        var incomeSlices = BuildSlices(incomeCategories);

        var expenseCategories = groups
            .SelectMany(g => g.Categories)
            .Select(c => (c.Name, Amount: Math.Abs(c.LineItems.Where(li => li.Amount < 0 && !li.IsCashflowOnly).Sum(li => li.Amount))))
            .Where(c => c.Amount > 0)
            .OrderByDescending(c => c.Amount)
            .ToList();

        if (vatExpenses > 0)
            expenseCategories.Add((Resources.GetString("Budget_VatLiability", CultureInfo.CurrentUICulture)!, vatExpenses));

        var profit = income + vatCredits - (Math.Abs(expenses) + vatExpenses);
        if (profit > 0)
        {
            expenseCategories.Add((Resources.GetString("Budget_CashReserves", CultureInfo.CurrentUICulture)!, profit * 0.9m));
            expenseCategories.Add((Resources.GetString("Budget_SpanishTaxes", CultureInfo.CurrentUICulture)!, profit * 0.1m));
        }

        var expenseSlices = BuildSlices(expenseCategories);

        return new BudgetSummaryResult
        {
            TotalIncome = totalIncome,
            TotalExpenses = totalExpenses,
            NetBalance = netBalance,
            TotalLineItems = budgetLineItems.Sum(li => li.Amount),
            IncomeSlices = incomeSlices,
            ExpenseSlices = expenseSlices
        };
    }

    internal static BudgetSummaryResult ComputeBudgetSummaryWithBuffers(IReadOnlyList<BudgetGroupDetail> groups)
    {
        var summary = ComputeBudgetSummary(groups);

        // Allocated minus line-item total (negative = expense, positive = income).
        var groupBuffers = groups
            .Where(g => !g.IsTicketingGroup)
            .Select(g => new
            {
                Name = string.Format(CultureInfo.CurrentCulture,
                    Resources.GetString("Budget_GroupBuffer", CultureInfo.CurrentUICulture)!, g.Name),
                Raw = g.Categories.Sum(c =>
                    c.AllocatedAmount - c.LineItems.Where(li => !li.IsCashflowOnly).Sum(li => li.Amount))
            })
            .Where(b => b.Raw != 0)
            .ToList();

        var allExpenseEntries = summary.ExpenseSlices
            .Select(s => (s.Name, s.Amount))
            .Concat(groupBuffers.Where(b => b.Raw < 0).Select(b => (b.Name, Amount: Math.Abs(b.Raw))))
            .ToList();

        var allIncomeEntries = summary.IncomeSlices
            .Select(s => (s.Name, s.Amount))
            .Concat(groupBuffers.Where(b => b.Raw > 0).Select(b => (b.Name, Amount: b.Raw)))
            .ToList();

        return new BudgetSummaryResult
        {
            TotalIncome = summary.TotalIncome,
            TotalExpenses = summary.TotalExpenses,
            NetBalance = summary.NetBalance,
            TotalLineItems = summary.TotalLineItems,
            IncomeSlices = BuildSlices(allIncomeEntries),
            ExpenseSlices = BuildSlices(allExpenseEntries)
        };
    }

    private static IReadOnlyList<BudgetSliceResult> BuildSlices(
        IReadOnlyCollection<(string Name, decimal Amount)> entries)
    {
        var total = entries.Sum(entry => entry.Amount);
        return entries.Select(entry => new BudgetSliceResult
        {
            Name = entry.Name,
            Amount = entry.Amount,
            Percentage = total > 0 ? entry.Amount / total * 100 : 0
        }).ToList();
    }

}
