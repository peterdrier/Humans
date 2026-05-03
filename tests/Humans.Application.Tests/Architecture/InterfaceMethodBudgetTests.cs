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
/// Method-count budget for major service interfaces. This is a
/// **consolidation ratchet** — the goal is for budgeted interfaces to get
/// smaller over time, not stable, not redistributed.
///
/// Agent rules (strict):
/// - No raises. Adding a method requires removing one from the SAME
///   interface in the SAME PR. Net delta is ≤ 0.
/// - No splits as a workaround. Don't extract a sub-interface to put methods
///   under a fresh budget — that defeats the consolidation goal.
/// - No "replace 2 methods with 1 broader bag-of-flags method" tricks. The
///   count drops but the surface grows.
/// - When net delta is negative, lower the budget number to match the new
///   count exactly. Budgets_are_tight_and_not_padded forbids headroom.
/// - Hit a wall? STOP and ask the repo owner. Only the owner authorizes
///   raises, and only out-of-band — never preemptively in a PR.
///
/// Why: the audit-surface skill kept finding bloat that accrued one
/// "this addition is justified, +1" PR at a time. Every raise had a
/// justification. A split-it escape hatch redistributes the same surface
/// across two budgets with fresh growth runway in each. The mechanism only
/// works when agents cannot reach for either lever on their own initiative.
///
/// In scope: interfaces with a meaningful surface (≥10 methods) where
/// growth would matter. Smaller interfaces aren't budgeted — adding the 3rd
/// method to a 2-method interface isn't a smell.
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
        // 71→70: account-merge fold redesign — removed ReassignToUserAsync
        // from ITeamService (moved to IUserMerge.ReassignAsync, implemented
        // by TeamService and dispatched by AccountMergeService via
        // IEnumerable<IUserMerge> fan-out).
        [typeof(ITeamService)] = 70,
        // ICampService raised 53→57 for per-camp roles feature (peterdrier#489):
        // AddCampMemberAsLeadAsync, GetSeasonMembersAsync, GetCampMemberStatusAsync,
        // GetCampSeasonsForComplianceAsync — all needed by ICampRoleService and the
        // Camp Edit page roles panel.
        // 57→56: simplify pass — added BuildCampDetailDataAsync, replaced 3 scoped
        // CampSeason getters (SoundZone/Name/Info) with single GetCampSeasonByIdAsync.
        // 56→55: collapsed GetCampsForYearAsync + GetAllCampsForYearAsync into one
        // method; callers filter via Camp.IsPublic predicate.
        // 55→55: account-merge fold redesign Phase 3.3. Added
        // ReassignAssignmentsToUserAsync; removed GetCampByIdAsync (pure
        // passthrough to ICampRepository.GetByIdAsync — zero production
        // callers, zero tests, zero internal callers; CampDetail/Edit
        // flows resolve by slug, not id).
        // 55→54: account-merge fold final consolidation — removed
        // ReassignAssignmentsToUserAsync from ICampService (moved to
        // IUserMerge.ReassignAsync, dispatched via fan-out).
        // 54→51: barrio-mgmt-fixes audit (peterdrier#390). Net -3 after
        // adding AddMemberAndAssignRoleAsync (+1) and removing 4 dead methods:
        // GetCampDetailAsync (zero prod callers — controllers compose
        // GetCampBySlugAsync + BuildCampDetailDataAsync directly; the two
        // tests were repointed to the same composition);
        // GetCampsByLeadUserIdAsync (zero callers — pure passthrough; the
        // new lead-pin feature on this branch already calls the repo
        // method directly); SetSeasonFullAsync and
        // GetCampSeasonBriefsForYearAsync (both zero callers, zero tests).
        [typeof(ICampService)] = 51,
        // +1: GetOverallCoverageAsync for admin dashboard shift-coverage tile (peterdrier#349).
        // 50→50: account-merge fold redesign Phase 3.2. Added
        // ReassignProfilesAndTagPrefsToUserAsync; removed CanManageShiftsAsync
        // (zero production callers, zero tests — fully dead since the
        // shift-management slice 1/2 plan that introduced it never wired it
        // up; controllers use IsDeptCoordinatorAsync + role checks directly).
        // 50→49: account-merge fold final consolidation — removed
        // ReassignProfilesAndTagPrefsToUserAsync from IShiftManagementService
        // (moved to IUserMerge.ReassignAsync, dispatched via fan-out).
        [typeof(IShiftManagementService)] = 49,
        // +1 for SetProfilePictureAsync (nobodies-collective/Humans#532 — Google avatar import button needs a
        // narrow service write that owns its own cache invalidation; controllers can't reach
        // the FullProfile cache directly).
        // +1 for GetActiveApprovedCountAsync (admin dashboard active-humans tile, peterdrier#349).
        // 41→41: account-merge fold redesign Phase 1.2. Added
        // ReassignSubAggregatesToUserAsync; removed
        // GetActiveOrCompletedCampaignGrantsAsync (pure passthrough to
        // ICampaignService.GetActiveOrCompletedGrantsForUserAsync — sole
        // caller ProfileController already injects ICampaignService and now
        // calls it directly).
        // 41→40: account-merge fold final consolidation — removed
        // ReassignSubAggregatesToUserAsync from IProfileService (moved to
        // IUserMerge.ReassignAsync, dispatched via fan-out).
        // 40→39: barrio-mgmt-fixes audit (peterdrier#390). Net -1 after
        // adding SearchHumansByNameAsync (+1) and merging two pairs of
        // sibling state-setters (-2):
        // ClearConsentCheckAsync + FlagConsentCheckAsync → RecordConsentCheckAsync
        // (takes a ConsentCheckStatus result; the system-driven
        // SetConsentCheckPendingAsync stays separate — different actor).
        // SuspendAsync + UnsuspendAsync → SetSuspendedAsync (takes a bool).
        [typeof(IProfileService)] = 39,
        // -1 for GetContactUsersAsync removal (/Contacts surface deleted in PR 2 of
        // email-identity-decoupling — only ContactService called it).
        // 31→31: account-merge fold redesign Phase 3.4. Added 3 fold primitives
        // (AnonymizeForMergeAsync, ReassignLoginsToUserAsync,
        // ReassignEventParticipationToUserAsync); removed 3 to match.
        // Removed: SetGoogleEmailStatusAsync (interface-surface-dead — sole
        // external caller in GoogleWorkspaceSyncService is sync-driven and
        // routes through TrySetGoogleEmailStatusFromSyncAsync, which the
        // Rejected-is-terminal guard short-circuits identically; impl keeps
        // a private helper). BackfillNobodiesTeamGoogleEmailsAsync (sole
        // caller SystemTeamSyncJob.BackfillGoogleEmailsAsync now iterates
        // per-user via IUserEmailService.TryBackfillGoogleEmailAsync, which
        // already exists). GetAllUserIdsAsync (4 callers replaced with
        // (await GetAllUsersAsync(ct)).Select(u => u.Id) — at ~500-user
        // scale the extra User-entity hydration is cheap per design rules).
        // 31→31: account-merge fold redesign Phase 4.1. Added
        // GetMergedSourceIdsAsync (the chain-follow service primitive that
        // AuditLog/Consent/BudgetAuditLog reads call to surface rows still
        // attributed to merged source tombstones); removed 1 to match.
        // Removed: GetPendingDeletionCountAsync. Three callers (admin daily
        // digest, board daily digest, NotificationMeterProvider) each
        // already load — or can cheaply load — the full user list and
        // derive the count in-memory as
        // allUsers.Count(u => u.DeletionRequestedAt != null). The two
        // digest jobs already had `allUsers` in scope; the meter provider
        // is itself cached for ~2 minutes (CacheKeys.NotificationMeters)
        // so loading the user list per cache window is acceptable at
        // ~500-user scale per design rules.
        // 31→29: account-merge fold final consolidation — removed
        // ReassignLoginsToUserAsync and ReassignEventParticipationToUserAsync
        // from IUserService. Login move and event-participation move now
        // both happen through IUserMerge.ReassignAsync on UserService;
        // DuplicateAccountService routes the logins move directly via
        // IUserRepository (it doesn't run the full IUserMerge fan-out).
        [typeof(IUserService)] = 29,
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
