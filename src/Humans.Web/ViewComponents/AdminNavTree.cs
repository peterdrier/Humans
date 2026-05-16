using Humans.Web.Authorization;

namespace Humans.Web.ViewComponents;

/// <summary>
/// The configured admin sidebar tree. Order is by daily-traffic-across-the-whole-
/// admin-audience, NOT by structural prominence (so Voting/Review do NOT appear at
/// the top). See feedback memory: voting/review serve ~8 people, not the 800
/// humans on the platform.
/// </summary>
public static class AdminNavTree
{
    public static IReadOnlyList<AdminNavGroup> Groups { get; } =
    [
        new("Operations", [
            new("Tickets",            "Ticket",         "Index", null, null, "fa-solid fa-ticket",      PolicyNames.TicketAdminBoardOrAdmin),
            new("Transfer requests",  "TicketTransferAdmin", "Index", null, null, "fa-solid fa-right-left",  PolicyNames.TicketAdminOrAdmin,
                 PillCount: PillCounts.TransferQueue),
            new("Scanner",            "Scanner",        "Index", null, null, "fa-solid fa-qrcode",      PolicyNames.TicketAdminBoardOrAdmin)
        ]),
        new("Members", [
            new("Humans", "Profile", "AdminList",       null, null, "fa-solid fa-users",            PolicyNames.HumanAdminBoardOrAdmin),
            new("Review", "OnboardingReview", "Index",   null, null, "fa-solid fa-clipboard-check",  PolicyNames.ReviewQueueAccess,
                 PillCount: PillCounts.ReviewQueue)
        ]),
        new("Money", [
            new("Finance",        "Finance",      "Index",   null, null, "fa-solid fa-coins",     PolicyNames.FinanceAdminOrAdmin),
            new("Store catalog",  "StoreAdmin",   "Catalog", null, null, "fa-solid fa-tags",      PolicyNames.StoreCatalogAdmin)
        ]),
        new("Expenses", [
            new("Expenses",          "Expenses", "Index",       null, null, "fa-solid fa-receipt",        PolicyNames.IsActiveMember),
            new("Coordinator Queue", "Expenses", "Coordinator", null, null, "fa-solid fa-list-check",     PolicyNames.IsActiveMember),
            new("Expense Review",    "Expenses", "Review",      null, null, "fa-solid fa-magnifying-glass-dollar", PolicyNames.FinanceAdminOrAdmin)
        ]),
        new("Events", [
            new("Guide settings", "EventsAdmin", "Settings", null, null, "fa-solid fa-calendar-days", PolicyNames.EventsAdminOrAdmin),
            new("Guide categories", "EventsAdmin", "Categories", null, null, "fa-solid fa-tags", PolicyNames.EventsAdminOrAdmin),
            new("Guide venues", "EventsAdmin", "Venues", null, null, "fa-solid fa-location-dot", PolicyNames.EventsAdminOrAdmin)
        ]),
        new("Governance", [
            new("Voting", "GovernanceBoardVoting", "BoardVoting", null, null, "fa-solid fa-check-to-slot", PolicyNames.BoardOrAdmin,
                 PillCount: PillCounts.VotingQueue),
            new("Applications", "GovernanceApplications", "Admin", null, null, "fa-solid fa-file-signature", PolicyNames.BoardOrAdmin),
            new("Roles",        "Governance",  "Roles",            null, null, "fa-solid fa-id-badge",       PolicyNames.BoardOrAdmin),
            new("Audit log",    "AuditLog",    "Index",            null, null, "fa-solid fa-book-open",      PolicyNames.BoardOrAdmin)
        ]),
        new("Integrations", [
            new("Google",             "Google", "Index",        null, null, "fa-brands fa-google",   PolicyNames.AdminOnly),
            new("Email preview",      "Email",  "EmailPreview", null, null, "fa-solid fa-envelope",  PolicyNames.AdminOnly),
            new("Email outbox",       "Email",  "EmailOutbox",  null, null, "fa-solid fa-inbox",     PolicyNames.AdminOnly),
            new("Campaigns",          "Campaign", "Index",      null, null, "fa-solid fa-bullhorn",  PolicyNames.AdminOnly),
            new("Workspace accounts", "Google",  "Accounts",    null, null, "fa-solid fa-at",        PolicyNames.AdminOnly),
            new("Email flag violations", "Google", "EmailFlagViolations", null, null, "fa-solid fa-triangle-exclamation", PolicyNames.AdminOnly),
            new("Mailer",                "MailerAdmin", "Index",           null, null, "fa-solid fa-paper-plane",          PolicyNames.AdminOnly)
        ]),
        new("Agent", [
            new("Agent Status",  "AdminAgent", "Status",        null, null, "fa-solid fa-gauge-high", PolicyNames.AdminOnly),
            new("Agent Config",  "AdminAgent", "Settings",      null, null, "fa-solid fa-robot",      PolicyNames.AdminOnly),
            new("Agent History", "Agent",      "Conversations", null, null, "fa-solid fa-comments",   PolicyNames.AdminOnly)
        ]),
        new("People data", [
            new("Merge requests",        "AdminMerge", "Index",            null, null, "fa-solid fa-code-merge", PolicyNames.AdminOnly),
            new("Duplicate detection",   "AdminDuplicateAccounts", "Index", null, null, "fa-solid fa-clone",      PolicyNames.AdminOnly),
            new("Email problems",        "ProfileAdmin", "EmailProblems", null, null, "fa-solid fa-envelope-circle-check", PolicyNames.AdminOnly),
            new("Audience segmentation", "Admin", "AudienceSegmentation",   null, null, "fa-solid fa-chart-pie",  PolicyNames.AdminOnly),
            new("Legal documents",       "AdminLegalDocuments", "LegalDocuments", null, null, "fa-solid fa-scale-balanced", PolicyNames.AdminOnly),
            new("Backfill Provider/IsGoogle", "Admin", "BackfillUserEmailProviders", null, null, "fa-solid fa-key", PolicyNames.AdminOnly),
            new("Stub Profile Backfill",      "ProfileBackfillAdmin", "Index",       null, null, "fa-solid fa-user-plus", PolicyNames.AdminOnly)
        ]),
        new("Diagnostics", [
            new("Logs",            "Admin", "Logs",          null, null, "fa-solid fa-triangle-exclamation", PolicyNames.AdminOnly),
            new("DB stats",        "Admin", "DbStats",       null, null, "fa-solid fa-database",            PolicyNames.AdminOnly),
            new("Cache stats",     "Admin", "CacheStats",    null, null, "fa-solid fa-bolt",                PolicyNames.AdminOnly),
            new("All users (debug)", "UsersAdminDebug", "Index", null, null, "fa-solid fa-bug-slash", PolicyNames.AdminOnly),
            new("Configuration",   "Admin", "Configuration", null, null, "fa-solid fa-gear",                PolicyNames.AdminOnly),
            new("Maintenance",     "Admin", "Maintenance",   null, null, "fa-solid fa-screwdriver-wrench",  PolicyNames.AdminOnly),
            new("Orphan signups",  "Shifts", "OrphanSignups", null, null, "fa-solid fa-user-secret",        PolicyNames.AdminOnly),
            new("Picture migration", "ProfilePictureMigrationAdmin", "Index", null, null, "fa-solid fa-image", PolicyNames.AdminOnly),
            new("Hangfire",        null, null, null, "/hangfire",      "fa-solid fa-clock-rotate-left", PolicyNames.AdminOnly),
            new("Health",          null, null, null, "/health/ready",  "fa-solid fa-heart-pulse",       PolicyNames.AdminOnly)
        ]),
        new("Dev", [
            new("Seed budget",     "DevSeed", "SeedBudget",    null, null, "fa-solid fa-coins",     PolicyNames.AdminOnly,
                 EnvironmentGate: env => !env.IsProduction()),
            new("Seed camp roles", "DevSeed", "SeedCampRoles", null, null, "fa-solid fa-user-tag",  PolicyNames.AdminOnly,
                 EnvironmentGate: env => !env.IsProduction())
        ])
    ];
}

internal static class PillCounts
{
    public static async ValueTask<int?> ReviewQueue(IServiceProvider sp)
    {
        var adminDashboard = sp.GetRequiredService<Application.Interfaces.Dashboard.IAdminDashboardService>();
        var count = await adminDashboard.GetPendingReviewCountAsync();
        return count > 0 ? count : null;
    }

    public static async ValueTask<int?> VotingQueue(IServiceProvider sp)
    {
        var http = sp.GetRequiredService<IHttpContextAccessor>();
        var idClaim = http.HttpContext?.User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier);
        if (idClaim is null || !Guid.TryParse(idClaim.Value, out var userId))
            return null;
        var applications = sp.GetRequiredService<Application.Interfaces.Governance.IApplicationDecisionService>();
        var count = await applications.GetUnvotedApplicationCountAsync(userId);
        return count > 0 ? count : null;
    }

    public static async ValueTask<int?> TransferQueue(IServiceProvider sp)
    {
        var transfers = sp.GetRequiredService<Application.Interfaces.Tickets.ITicketTransferService>();
        var count = await transfers.CountPendingAsync();
        return count > 0 ? count : null;
    }
}
