using System.Reflection;
using AwesomeAssertions;
using Humans.Application.Interfaces.Camps;
using Humans.Application.Interfaces.Governance;
using Humans.Application.Interfaces.Profiles;
using Humans.Application.Interfaces.Shifts;
using Humans.Application.Interfaces.Teams;
using Humans.Application.Interfaces.Users;
using Xunit;

namespace Humans.Application.Tests.Architecture;

/// <summary>
/// Method-count budget for major service interfaces. Each entry is a hard ceiling
/// pinned at the count when the test was last updated. CI fails if any interface
/// grows past its budget — adding a new method to a budgeted interface requires
/// either deleting an existing one in the same PR (decrement-only ratchet) or
/// raising the budget here with a one-line justification in the PR.
///
/// Why: the audit-surface skill keeps finding bloat that nobody noticed accruing
/// because each PR only adds 1-2 methods. Without a tripwire, the only feedback
/// loop is a periodic audit. This makes growth visible at the moment it happens.
///
/// What's in scope: interfaces with a meaningful surface (≥10 methods) where
/// growth would matter. Smaller interfaces aren't budgeted — adding the 3rd
/// method to a 2-method interface isn't a smell.
///
/// What to do when this fails:
/// - If the new method is genuinely needed and you've already removed something:
///   lower the budget here to match the new count.
/// - If you can't remove anything but the addition is justified: raise the
///   budget here, document why in the PR description, and consider whether the
///   interface should be split (run /audit-surface).
/// - Don't bypass the test. The point is to make growth deliberate.
/// </summary>
public class InterfaceMethodBudgetTests
{
    /// <summary>
    /// Method-count ceilings per interface. Decrement when methods are
    /// deliberately removed; raise only with explicit justification.
    /// </summary>
    private static readonly IReadOnlyDictionary<Type, int> Budgets = new Dictionary<Type, int>
    {
        // Audited 2026-04-26 against reforge audit-surface 0.8.0
        [typeof(ITeamService)] = 71,
        [typeof(ICampService)] = 53,
        [typeof(IShiftManagementService)] = 49,
        [typeof(IProfileService)] = 39,
        [typeof(IUserService)] = 32,
    };

    [HumansTheory]
    [MemberData(nameof(BudgetedInterfaces))]
    public void Interface_method_count_does_not_exceed_budget(Type interfaceType)
    {
        var current = CountPublicMethods(interfaceType);
        var budget = Budgets[interfaceType];

        current.Should().BeLessThanOrEqualTo(budget,
            because:
                $"{interfaceType.Name} has {current} methods, budget is {budget}. " +
                "Either remove a method in this PR (preferred — decrement budget to match), " +
                "or raise the budget with a one-line justification. " +
                "Run /audit-surface " + interfaceType.Name + " to see what could be eliminated.");
    }

    [HumansFact]
    public void Budgets_are_tight_and_not_padded()
    {
        // Catch the "raise the budget by 5 to leave headroom" anti-pattern.
        // Each budget should equal the current count, not exceed it. If a
        // budget has slack, the next addition slips past with no friction.
        foreach (var (type, budget) in Budgets)
        {
            var current = CountPublicMethods(type);
            current.Should().Be(budget,
                because:
                    $"{type.Name} budget ({budget}) should equal current count ({current}). " +
                    "When you remove a method, decrement the budget. The whole point of the " +
                    "ratchet is that there's no headroom to absorb future drift.");
        }
    }

    public static IEnumerable<object[]> BudgetedInterfaces() =>
        Budgets.Keys.Select(t => new object[] { t });

    private static int CountPublicMethods(Type interfaceType)
    {
        if (!interfaceType.IsInterface)
            throw new ArgumentException($"{interfaceType.Name} is not an interface.", nameof(interfaceType));

        // Direct method declarations only — exclude inherited interface methods
        // and property accessors. We're measuring surface area, not transitive
        // signature count.
        return interfaceType
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Count(m => !m.IsSpecialName);
    }
}
