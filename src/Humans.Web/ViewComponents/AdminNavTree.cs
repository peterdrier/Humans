using Humans.Domain.Constants;
using Humans.Web.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Humans.Web.ViewComponents;

/// <summary>
/// The configured admin sidebar tree. Order is by daily-traffic-across-the-whole-
/// admin-audience, NOT by structural prominence (so Voting/Review do NOT appear at
/// the top). See feedback memory: voting/review serve ~8 people, not the 800
/// humans on the platform.
/// </summary>
public static class AdminNavTree
{
    public static IReadOnlyList<AdminNavGroup> Groups { get; } = new AdminNavGroup[]
    {
        new("AdminGroup_Operations", new AdminNavItem[]
        {
            new("AdminNav_Volunteers", "Vol", "Index",       null, null, "fa-solid fa-people-group",     PolicyNames.VolunteerSectionAccess),
            new("AdminNav_Tickets",    "Ticket", "Index",    null, null, "fa-solid fa-ticket",            PolicyNames.TicketAdminBoardOrAdmin),
            new("AdminNav_Scanner",    "Scanner", "Index",   null, null, "fa-solid fa-qrcode",            PolicyNames.TicketAdminBoardOrAdmin),
        }),
        new("AdminGroup_Members", new AdminNavItem[]
        {
            new("AdminNav_Humans", "Profile", "AdminList",       null, null, "fa-solid fa-users",            PolicyNames.HumanAdminBoardOrAdmin),
            new("AdminNav_Review", "OnboardingReview", "Index",   null, null, "fa-solid fa-clipboard-check",  PolicyNames.ReviewQueueAccess,
                 PillCount: PillCounts.ReviewQueue),
        }),
        new("AdminGroup_Money", new AdminNavItem[]
        {
            new("AdminNav_Finance", "Finance", "Index", null, null, "fa-solid fa-coins", PolicyNames.FinanceAdminOrAdmin),
        }),
        new("AdminGroup_Governance", new AdminNavItem[]
        {
            new("AdminNav_Voting", "OnboardingReview", "BoardVoting", null, null, "fa-solid fa-check-to-slot", PolicyNames.BoardOrAdmin,
                 PillCount: PillCounts.VotingQueue),
            new("AdminNav_Board",  "Board", "Index",                  null, null, "fa-solid fa-gavel",          PolicyNames.BoardOrAdmin),
        }),
        new("AdminGroup_Integrations", new AdminNavItem[]
        {
            new("AdminNav_Google",            "Google", "Index",        null, null, "fa-brands fa-google",   PolicyNames.AdminOnly),
            new("AdminNav_EmailPreview",      "Email",  "EmailPreview", null, null, "fa-solid fa-envelope",  PolicyNames.AdminOnly),
            new("AdminNav_EmailOutbox",       "Email",  "EmailOutbox",  null, null, "fa-solid fa-inbox",     PolicyNames.AdminOnly),
            new("AdminNav_Campaigns",         "Campaign", "Index",      null, null, "fa-solid fa-bullhorn",  PolicyNames.AdminOnly),
            new("AdminNav_WorkspaceAccounts", "Google",  "Accounts",    null, null, "fa-solid fa-at",        PolicyNames.AdminOnly),
        }),
        new("AdminGroup_PeopleData", new AdminNavItem[]
        {
            new("AdminNav_Merge",          "AdminMerge", "Index",            null, null, "fa-solid fa-code-merge", PolicyNames.AdminOnly),
            new("AdminNav_Duplicates",     "AdminDuplicateAccounts", "Index", null, null, "fa-solid fa-clone",      PolicyNames.AdminOnly),
            new("AdminNav_Audience",       "Admin", "AudienceSegmentation",   null, null, "fa-solid fa-chart-pie",  PolicyNames.AdminOnly),
            new("AdminNav_LegalDocuments", "AdminLegalDocuments", "Index",    null, null, "fa-solid fa-scale-balanced", PolicyNames.AdminOnly),
        }),
        new("AdminGroup_Diagnostics", new AdminNavItem[]
        {
            new("AdminNav_Logs",          "Admin", "Logs",          null, null, "fa-solid fa-triangle-exclamation", PolicyNames.AdminOnly),
            new("AdminNav_DbStats",       "Admin", "DbStats",       null, null, "fa-solid fa-database",            PolicyNames.AdminOnly),
            new("AdminNav_CacheStats",    "Admin", "CacheStats",    null, null, "fa-solid fa-bolt",                PolicyNames.AdminOnly),
            new("AdminNav_Configuration", "Admin", "Configuration", null, null, "fa-solid fa-gear",                PolicyNames.AdminOnly),
            new("AdminNav_Hangfire",      null, null, null, "/hangfire",      "fa-solid fa-clock-rotate-left", PolicyNames.AdminOnly),
            new("AdminNav_Health",        null, null, null, "/health/ready",  "fa-solid fa-heart-pulse",       PolicyNames.AdminOnly),
        }),
        new("AdminGroup_Dev", new AdminNavItem[]
        {
            new("AdminNav_SeedBudget",    "DevSeed", "SeedBudget",    null, null, "fa-solid fa-coins",     PolicyNames.AdminOnly,
                 EnvironmentGate: env => !env.IsProduction()),
            new("AdminNav_SeedCampRoles", "DevSeed", "SeedCampRoles", null, null, "fa-solid fa-user-tag",  PolicyNames.AdminOnly,
                 EnvironmentGate: env => !env.IsProduction()),
        }),
    };
}

internal static class PillCounts
{
    public static async ValueTask<int?> ReviewQueue(IServiceProvider sp)
    {
        var onboarding = sp.GetRequiredService<Humans.Application.Interfaces.Onboarding.IOnboardingService>();
        var count = await onboarding.GetPendingReviewCountAsync();
        return count > 0 ? count : null;
    }

    public static async ValueTask<int?> VotingQueue(IServiceProvider sp)
    {
        var http = sp.GetRequiredService<IHttpContextAccessor>();
        var idClaim = http.HttpContext?.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (idClaim is null || !Guid.TryParse(idClaim.Value, out var userId))
            return null;
        var onboarding = sp.GetRequiredService<Humans.Application.Interfaces.Onboarding.IOnboardingService>();
        var count = await onboarding.GetUnvotedApplicationCountAsync(userId);
        return count > 0 ? count : null;
    }
}
