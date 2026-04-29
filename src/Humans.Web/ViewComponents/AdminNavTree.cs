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
        new("Operations", new AdminNavItem[]
        {
            new("Volunteers", "Vol", "Index",       null, null, "fa-solid fa-people-group",     PolicyNames.VolunteerSectionAccess),
            new("Tickets",    "Ticket", "Index",    null, null, "fa-solid fa-ticket",            PolicyNames.TicketAdminBoardOrAdmin),
            new("Scanner",    "Scanner", "Index",   null, null, "fa-solid fa-qrcode",            PolicyNames.TicketAdminBoardOrAdmin),
        }),
        new("Members", new AdminNavItem[]
        {
            new("Humans", "Profile", "AdminList",       null, null, "fa-solid fa-users",            PolicyNames.HumanAdminBoardOrAdmin),
            new("Review", "OnboardingReview", "Index",   null, null, "fa-solid fa-clipboard-check",  PolicyNames.ReviewQueueAccess,
                 PillCount: PillCounts.ReviewQueue),
        }),
        new("Money", new AdminNavItem[]
        {
            new("Finance", "Finance", "Index", null, null, "fa-solid fa-coins", PolicyNames.FinanceAdminOrAdmin),
        }),
        new("Governance", new AdminNavItem[]
        {
            new("Voting", "OnboardingReview", "BoardVoting", null, null, "fa-solid fa-check-to-slot", PolicyNames.BoardOrAdmin,
                 PillCount: PillCounts.VotingQueue),
            new("Board",  "Board", "Index",                  null, null, "fa-solid fa-gavel",          PolicyNames.BoardOrAdmin),
        }),
        new("Integrations", new AdminNavItem[]
        {
            new("Google",             "Google", "Index",        null, null, "fa-brands fa-google",   PolicyNames.AdminOnly),
            new("Email preview",      "Email",  "EmailPreview", null, null, "fa-solid fa-envelope",  PolicyNames.AdminOnly),
            new("Email outbox",       "Email",  "EmailOutbox",  null, null, "fa-solid fa-inbox",     PolicyNames.AdminOnly),
            new("Campaigns",          "Campaign", "Index",      null, null, "fa-solid fa-bullhorn",  PolicyNames.AdminOnly),
            new("Workspace accounts", "Google",  "Accounts",    null, null, "fa-solid fa-at",        PolicyNames.AdminOnly),
        }),
        new("People data", new AdminNavItem[]
        {
            new("Merge requests",        "AdminMerge", "Index",            null, null, "fa-solid fa-code-merge", PolicyNames.AdminOnly),
            new("Duplicate detection",   "AdminDuplicateAccounts", "Index", null, null, "fa-solid fa-clone",      PolicyNames.AdminOnly),
            new("Audience segmentation", "Admin", "AudienceSegmentation",   null, null, "fa-solid fa-chart-pie",  PolicyNames.AdminOnly),
            new("Legal documents",       "AdminLegalDocuments", "LegalDocuments", null, null, "fa-solid fa-scale-balanced", PolicyNames.AdminOnly),
            new("Backfill UserEmails",   "Admin", "BackfillUserEmails",     null, null, "fa-solid fa-envelopes-bulk", PolicyNames.AdminOnly),
        }),
        new("Diagnostics", new AdminNavItem[]
        {
            new("Logs",          "Admin", "Logs",          null, null, "fa-solid fa-triangle-exclamation", PolicyNames.AdminOnly),
            new("DB stats",      "Admin", "DbStats",       null, null, "fa-solid fa-database",            PolicyNames.AdminOnly),
            new("Cache stats",   "Admin", "CacheStats",    null, null, "fa-solid fa-bolt",                PolicyNames.AdminOnly),
            new("Configuration", "Admin", "Configuration", null, null, "fa-solid fa-gear",                PolicyNames.AdminOnly),
            new("Maintenance",   "Admin", "Maintenance",   null, null, "fa-solid fa-screwdriver-wrench",  PolicyNames.AdminOnly),
            new("Hangfire",      null, null, null, "/hangfire",      "fa-solid fa-clock-rotate-left", PolicyNames.AdminOnly),
            new("Health",        null, null, null, "/health/ready",  "fa-solid fa-heart-pulse",       PolicyNames.AdminOnly),
        }),
        new("Dev", new AdminNavItem[]
        {
            new("Seed budget",     "DevSeed", "SeedBudget",    null, null, "fa-solid fa-coins",     PolicyNames.AdminOnly,
                 EnvironmentGate: env => !env.IsProduction()),
            new("Seed camp roles", "DevSeed", "SeedCampRoles", null, null, "fa-solid fa-user-tag",  PolicyNames.AdminOnly,
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
