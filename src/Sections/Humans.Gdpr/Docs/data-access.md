# Gdpr — Data Access

## Gdpr

Folder: `src/Sections/Humans.Gdpr/Services/`. No owned DB tables —
the export orchestrator runs over per-section `IUserDataContributor`
fan-out.

### GdprExportService (Scoped)

No repository. Injects `IEnumerable<IUserDataContributor>` — every
section that owns per-user tables implements this and contributes its
slice. Current contributors (per design-rules §8a): Users
(`UserService` + `AccountMergeService`), Auth
(`RoleAssignmentService`), Governance (`ApplicationDecisionService`),
Camps (`CampService`), Shifts (`ShiftSignupService`), Tickets
(`TicketQueryService` — the keyed inner), Notifications
(`NotificationInboxService`), AuditLog (`AuditLogService`), Budget
(`BudgetService`), Campaigns (`CampaignService`), Feedback
(`FeedbackService`), Issues (`IssuesService`), Events (`EventService`),
Expenses (`ExpenseReportService`), Finance (`HoldedFinanceService` —
creditor-contact binding), Agent (`AgentService`), Teams
(`TeamService`), Consent (`ConsentService`), Surveys (`SurveyService` —
identified responses only), Gate (`GateService` — data-minimized
gate-scan slice). No direct DB access, no cache.

---


