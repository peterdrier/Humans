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
/// Method-count budget for major service interfaces. **Strict down-only
/// ratchet for agents.** A PR that adds a method to a budgeted interface MUST
/// remove another in the same PR; net delta over the PR is ≤ 0. Agents must
/// not raise a number here for any reason — only the repo owner can authorize
/// a raise, and only after explicit out-of-band discussion. The bloat this
/// test exists to clean up was accrued one "this addition is justified, +1"
/// PR at a time; agent justifications are precisely the failure mode.
///
/// What's in scope: interfaces with a meaningful surface (≥10 methods) where
/// growth would matter. Smaller interfaces aren't budgeted — adding the 3rd
/// method to a 2-method interface isn't a smell.
///
/// What to do when this fails:
/// - You added a method without removing one. Remove one. Or replace the
///   added method with a refinement of an existing signature so the count
///   doesn't grow. Or split the interface (run /audit-surface).
/// - You removed methods and the count dropped: lower the budget here to
///   match the new count exactly. The Budgets_are_tight_and_not_padded test
///   forbids headroom.
/// - The interface genuinely needs to grow with no room to remove? STOP and
///   ask the repo owner before raising. Do not raise on your own initiative
///   under any circumstances. Do not pre-raise "to make room for later
///   work." The expected default is split-or-shrink, not raise.
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
        // ICampService raised 53→57 for per-camp roles feature (peterdrier#489):
        // AddCampMemberAsLeadAsync, GetSeasonMembersAsync, GetCampMemberStatusAsync,
        // GetCampSeasonsForComplianceAsync — all needed by ICampRoleService and the
        // Camp Edit page roles panel.
        // 57→56: simplify pass — added BuildCampDetailDataAsync, replaced 3 scoped
        // CampSeason getters (SoundZone/Name/Info) with single GetCampSeasonByIdAsync.
        [typeof(ICampService)] = 56,
        // +1: GetOverallCoverageAsync for admin dashboard shift-coverage tile (peterdrier#349).
        [typeof(IShiftManagementService)] = 50,
        // +1 for SetProfilePictureAsync (nobodies-collective/Humans#532 — Google avatar import button needs a
        // narrow service write that owns its own cache invalidation; controllers can't reach
        // the FullProfile cache directly).
        // +1 for GetActiveApprovedCountAsync (admin dashboard active-humans tile, peterdrier#349).
        [typeof(IProfileService)] = 41,
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
