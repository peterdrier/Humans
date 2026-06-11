using Humans.Web.Authorization;

namespace Humans.Web.ViewComponents;

/// <summary>
/// The configured admin sidebar tree. Order is by daily-traffic-across-the-whole-
/// admin-audience, NOT by structural prominence (so Voting/Review do NOT appear at
/// the top). See feedback memory: voting/review serve ~8 people, not the 800
/// humans on the platform.
///
/// Groups are filed by owning section (verify against docs/sections/_Index.md, not
/// by label vibes). Groups marked System: true are AdminOnly plumbing — they render
/// below a divider and start collapsed unless they hold the active page.
/// </summary>
public static class AdminNavTree
{
    public static IReadOnlyList<AdminNavGroup> Groups { get; } =
    [
        new("Tickets", [
            new("Tickets",            "Ticket",         "Index", null, null, "fa-solid fa-ticket",      PolicyNames.TicketAdminBoardOrAdmin),
            new("Transfer requests",  "TicketTransferAdmin", "Index", null, null, "fa-solid fa-right-left",  PolicyNames.TicketAdminOrAdmin,
                 PillCount: PillCounts.TransferQueue),
            new("Attendee contacts",  "TicketsContactsAdmin", "Index", null, null, "fa-solid fa-address-book", PolicyNames.TicketAdminOrAdmin),
            new("Onsite roster",      "TicketsOnsiteAdmin", "Index", null, null, "fa-solid fa-clipboard-list", PolicyNames.TicketAdminBoardOrAdmin),
            // Campaigns distribute ticket-vendor discount codes (email is just the
            // delivery channel) — Tickets, not Messaging. See docs/sections/Campaigns.md.
            new("Campaigns",          "Campaign",       "Index", null, null, "fa-solid fa-bullhorn",    PolicyNames.AdminOnly),
            new("Scanner",            "Scanner",        "Index", null, null, "fa-solid fa-qrcode",      PolicyNames.ScannerAccess),
            new("Gate terminal",      "TicketsGateAdmin", "Index", null, null, "fa-solid fa-key",       PolicyNames.TicketAdminOrAdmin),
            // Early Entry aggregates providers from Shifts, Teams AND Camps; its
            // consumers are gate ops (scanner door context), so it lives here.
            new("Early entry",        "EarlyEntryRoster", "Index", null, null, "fa-solid fa-door-open", PolicyNames.ShiftDashboardAccess)
        ]),
        new("Members", [
            new("Humans", "UsersAdmin", "AdminList",    null, null, "fa-solid fa-users",            PolicyNames.HumanAdminBoardOrAdmin),
            new("Roles",  "UsersAdmin", "Roles",        null, null, "fa-solid fa-id-badge",         PolicyNames.HumanAdminBoardOrAdmin),
            new("Review", "OnboardingReview", "Index",   null, null, "fa-solid fa-clipboard-check",  PolicyNames.ReviewQueueAccess,
                 PillCount: PillCounts.ReviewQueue),
            new("Account merges",      "UsersAdminAccountMerges", "Index", null, null, "fa-solid fa-code-merge", PolicyNames.AdminOnly),
            new("Email problems",      "ProfileAdmin", "EmailProblems",   null, null, "fa-solid fa-envelope-circle-check", PolicyNames.AdminOnly),
            // Read-only member-base segmentation stats (accounts × ticket × profile),
            // not a messaging tool.
            new("Audience segmentation", "UsersAdmin", "Audience", null, null, "fa-solid fa-chart-pie", PolicyNames.AdminOnly)
        ]),
        new("Shifts", [
            new("Dashboard", "ShiftDashboard", "Index",  null, null, "fa-solid fa-gauge",            PolicyNames.ShiftDepartmentManager),
            new("Summary by camp", "Shifts", "Summary", null, null, "fa-solid fa-campground",        PolicyNames.ShiftDepartmentManager),
            new("Volunteer tracking", "VolunteerTracking", "Index", null, null, "fa-solid fa-user-clock", PolicyNames.ShiftDashboardAccess),
            new("Workload",  "ShiftWorkloadAdmin", "Index",   null, null, "fa-solid fa-scale-unbalanced", PolicyNames.ShiftDashboardAccess),
            new("Post-event stats", "ShiftDashboard", "PostEventStats", null, null, "fa-solid fa-chart-bar", PolicyNames.ShiftDashboardAccess),
            new("Orphan signups",  "Shifts", "OrphanSignups", null, null, "fa-solid fa-user-secret",  PolicyNames.AdminOnly)
        ]),
        new("Barrios", [
            new("Overview",   "CampAdmin",      "Index",      null, null, "fa-solid fa-tents",           PolicyNames.CampAdminOrAdmin),
            new("Roles",      "CampAdmin",      "Roles",      null, null, "fa-solid fa-user-tag",        PolicyNames.CampAdminOrAdmin),
            new("Compliance", "CampCompliance", "Compliance", null, null, "fa-solid fa-clipboard-check", PolicyNames.CampComplianceAccess),
            // Page self-gates wider (city-planning team members too); they reach it
            // via the member-side City page, so the narrower nav policy is fine.
            new("Barrio map", "CityPlanning",   "Admin",      null, null, "fa-solid fa-map",             PolicyNames.CampAdminOrAdmin)
        ]),
        new("Cantina", [
            new("Roster", "Cantina", "Roster", null, null, "fa-solid fa-utensils", PolicyNames.CantinaAdminOrAdmin)
        ]),
        new("Money", [
            // Members' own expense pages (Index/Coordinator) are member-shell pages
            // linked from the member nav — only the finance review queue is admin.
            new("Expense review", "Expenses",   "Review",    null, null, "fa-solid fa-magnifying-glass-dollar", PolicyNames.FinanceAdminOrAdmin),
            new("Finance",        "Finance",    "Index",     null, null, "fa-solid fa-coins",        PolicyNames.FinanceAdminOrAdmin),
            new("Store catalog",  "StoreAdmin", "Catalog",   null, null, "fa-solid fa-tags",         PolicyNames.StoreCatalogAdmin),
            new("Store summary",  "StoreAdmin", "Summary",   null, null, "fa-solid fa-chart-column", PolicyNames.StoreCatalogAdmin),
            new("Store payments", "StoreAdmin", "Payments",  null, null, "fa-solid fa-credit-card",  PolicyNames.StoreCatalogAdmin)
        ]),
        new("Event Guide", [
            new("Dashboard",  "EventsDashboard",  "Index",      null, null, "fa-solid fa-chart-line",    PolicyNames.EventsAdminOrAdmin),
            new("Moderation", "EventsModeration", "Index",      null, null, "fa-solid fa-gavel",         PolicyNames.EventsAdminOrAdmin),
            new("Settings",   "EventsAdmin",      "Settings",   null, null, "fa-solid fa-calendar-days", PolicyNames.EventsAdminOrAdmin),
            new("Categories", "EventsAdmin",      "Categories", null, null, "fa-solid fa-tags",          PolicyNames.EventsAdminOrAdmin),
            new("Venues",     "EventsAdmin",      "Venues",     null, null, "fa-solid fa-location-dot",  PolicyNames.EventsAdminOrAdmin),
            new("Export",     "EventsExport",     "Index",      null, null, "fa-solid fa-file-export",   PolicyNames.EventsAdminOrAdmin)
        ]),
        new("Governance", [
            new("Voting", "GovernanceBoardVoting", "BoardVoting", null, null, "fa-solid fa-check-to-slot", PolicyNames.BoardOrAdmin,
                 PillCount: PillCounts.VotingQueue),
            new("Applications", "GovernanceApplications", "Admin", null, null, "fa-solid fa-file-signature", PolicyNames.BoardOrAdmin)
        ]),
        // Audit is a Crosscut (memory/architecture/crosscut-purity.md), not Governance —
        // Board usage is audience, never ownership (memory/architecture/governance-scope.md).
        new("Audit", [
            new("Audit log", "AuditLog", "Index", null, null, "fa-solid fa-book-open", PolicyNames.BoardOrAdmin)
        ]),
        new("Feedback", [
            new("Feedback queue", "Feedback", "Index", null, null, "fa-solid fa-comment-dots", PolicyNames.FeedbackAdminOrAdmin,
                 PillCount: PillCounts.FeedbackQueue),
            new("Issues",         "Issues",   "Index", null, null, "fa-solid fa-bug",          PolicyNames.AdminOnly)
        ]),
        new("Messaging", [
            new("Email preview",      "Email",  "EmailPreview", null, null, "fa-solid fa-envelope",  PolicyNames.AdminOnly),
            new("Email outbox",       "Email",  "EmailOutbox",  null, null, "fa-solid fa-inbox",     PolicyNames.AdminOnly),
            new("Mailer",             "MailerAdmin", "Index",   null, null, "fa-solid fa-paper-plane", PolicyNames.AdminOnly),
            // First-party survey tool (own section); Board happens to be its main
            // user today, but it is not Governance.
            new("Surveys", "SurveyAdmin", "Index", null, null, "fa-solid fa-square-poll-vertical", PolicyNames.BoardOrAdmin)
        ]),
        new("Google", System: true, Items: [
            new("Overview",              "Google", "Index",        null, null, "fa-brands fa-google",           PolicyNames.AdminOnly),
            new("Sync settings",         "Google", "SyncSettings", null, null, "fa-solid fa-sliders",           PolicyNames.AdminOnly),
            new("Resource sync",         "Google", "Sync",         null, null, "fa-solid fa-arrows-rotate",     PolicyNames.TeamsAdminBoardOrAdmin),
            new("All domain groups",     "Google", "AllGroups",    null, null, "fa-solid fa-globe",             PolicyNames.AdminOnly),
            new("Workspace accounts",    "Google", "Accounts",     null, null, "fa-solid fa-at",                PolicyNames.AdminOnly),
            new("Sync outbox",           "Google", "SyncOutbox",   null, null, "fa-solid fa-clock-rotate-left", PolicyNames.AdminOnly),
            new("Sync results",          "Google", "SyncResults",  null, null, "fa-solid fa-list-check",        PolicyNames.AdminOnly),
            new("Group settings",        "Google", "GroupSettingsResults", null, null, "fa-solid fa-gears",     PolicyNames.AdminOnly),
            new("Email renames",         "Google", "EmailRenames", null, null, "fa-solid fa-right-left",        PolicyNames.AdminOnly),
            new("Email flag violations", "Google", "EmailFlagViolations", null, null, "fa-solid fa-triangle-exclamation", PolicyNames.AdminOnly)
        ]),
        new("Agent", System: true, Items: [
            new("Status",  "AdminAgent", "Status",        null, null, "fa-solid fa-gauge-high", PolicyNames.AdminOnly),
            new("Config",  "AdminAgent", "Settings",      null, null, "fa-solid fa-robot",      PolicyNames.AdminOnly),
            new("History", "Agent",      "Conversations", null, null, "fa-solid fa-comments",   PolicyNames.AdminOnly)
        ]),
        new("Legal", System: true, Items: [
            new("Legal documents", "AdminLegalDocuments", "LegalDocuments", null, null, "fa-solid fa-scale-balanced", PolicyNames.AdminOnly)
        ]),
        new("Diagnostics", System: true, Items: [
            new("Logs",            "Debug", "Logs",          null, null, "fa-solid fa-triangle-exclamation", PolicyNames.AdminOnly),
            new("DB stats",        "Debug", "DbStats",       null, null, "fa-solid fa-database",            PolicyNames.AdminOnly),
            new("Cache stats",     "Debug", "CacheStats",    null, null, "fa-solid fa-bolt",                PolicyNames.AdminOnly),
            new("Client stats",    "Debug", "ClientStats",   null, null, "fa-solid fa-display",             PolicyNames.AdminOnly),
            new("Timings",         "Debug", "Timings",       null, null, "fa-solid fa-stopwatch",           PolicyNames.AdminOnly),
            new("All users (debug)", "UsersAdminDebug", "Index", null, null, "fa-solid fa-bug-slash", PolicyNames.AdminOnly),
            new("Configuration",   "Debug", "Configuration", null, null, "fa-solid fa-gear",                PolicyNames.AdminOnly),
            new("Maintenance",     "Debug", "Maintenance",   null, null, "fa-solid fa-screwdriver-wrench",  PolicyNames.AdminOnly),
            new("Hangfire",        null, null, null, "/hangfire",      "fa-solid fa-clock-rotate-left", PolicyNames.AdminOnly),
            new("Health",          null, null, null, "/health/ready",  "fa-solid fa-heart-pulse",       PolicyNames.AdminOnly)
        ]),
        new("Dev", System: true, Items: [
            new("Seed budget",     "DevSeed", "SeedBudget",    null, null, "fa-solid fa-coins",     PolicyNames.AdminOnly,
                 EnvironmentGate: env => !env.IsProduction()),
            new("Seed camp roles", "DevSeed", "SeedCampRoles", null, null, "fa-solid fa-user-tag",  PolicyNames.AdminOnly,
                 EnvironmentGate: env => !env.IsProduction())
        ]),
        new("Design", System: true, Items: [
            new("Color palette", "ColorPalette",  "Index", null, null, "fa-solid fa-palette", PolicyNames.AdminOnly),
            new("Components",    "WidgetGallery", "Index", null, null, "fa-solid fa-shapes",  PolicyNames.AdminOnly),
            new("Date formats",  "Debug",         "FormatGallery", null, null, "fa-solid fa-clock", PolicyNames.AdminOnly)
        ]),
        new("Temp", System: true, Items: [
            new("Picture migration",          "ProfilePictureMigrationAdmin", "Index", null, null, "fa-solid fa-image",     PolicyNames.AdminOnly),
            new("Stub profile backfill",      "ProfileBackfillAdmin", "Index",          null, null, "fa-solid fa-user-plus", PolicyNames.AdminOnly)
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
        var applications = sp.GetRequiredService<Application.Interfaces.Governance.IApplicationServiceRead>();
        var count = await applications.GetUnvotedApplicationCountAsync(userId);
        return count > 0 ? count : null;
    }

    public static async ValueTask<int?> TransferQueue(IServiceProvider sp)
    {
        var transfers = sp.GetRequiredService<Application.Interfaces.Tickets.ITicketTransferService>();
        var count = await transfers.CountPendingAsync();
        return count > 0 ? count : null;
    }

    public static async ValueTask<int?> FeedbackQueue(IServiceProvider sp)
    {
        var feedback = sp.GetRequiredService<Application.Interfaces.Feedback.IFeedbackService>();
        var count = await feedback.GetActionableCountAsync(CancellationToken.None);
        return count > 0 ? count : null;
    }
}
