namespace Humans.Web.Models;

/// <summary>
/// Hardcoded markdown content for section help pages (Guide, Glossary).
/// Content is updated via code releases — no admin editing.
/// </summary>
public static class SectionHelpContent
{
    public static string? GetGuide(string section) =>
        Guides.GetValueOrDefault(section);

    public static string? GetGlossary(string section) =>
        Glossaries.GetValueOrDefault(section);

    private static readonly Dictionary<string, string> Guides = new(StringComparer.Ordinal)
    {
        ["Teams"] = """
            ## How Teams Work

            Teams are how Nobodies Collective organises humans around shared interests and responsibilities. Every active human can browse teams and request to join.

            ### Joining a Team

            1. Browse the **Teams** page to see all available teams
            2. Click on a team to view its details and current members
            3. Click **Join** to become a member
            4. Some teams are managed — a coordinator may review your request

            ### Team Roles

            - **Member** — standard team participant
            - **Coordinator** — manages the team, can add/remove members and assign roles

            ### What Happens Automatically

            - When you join a team that has a **Google Group**, you are automatically added to that group
            - When you leave a team, you are automatically removed from the linked Google Group
            - When you join a team that has a **Shared Drive**, you are granted access automatically
            - Team changes sync to Google resources hourly via the background sync job

            ### Searching for Humans

            Use the **Search** button to find humans across all teams by name or email. The **Roster** view shows all active humans in a flat list. The **Map** shows humans by location. **Birthdays** shows upcoming celebrations.
            """,

        ["Profile"] = """
            ## Your Profile

            Your profile is how other humans in the collective see you. Keep it up to date so people can find and contact you.

            ### Completing Your Profile

            1. Add your **display name** — this is how you appear across the system
            2. Upload a **photo** so humans can recognise you
            3. Set your **birthday** (month and day) — the collective celebrates birthdays together
            4. Add **contact fields** (phone, social media, etc.) with visibility controls

            ### Contact Visibility

            Each contact field has a visibility level that controls who can see it:

            - **All active humans** — visible to every active member
            - **My teams** — visible only to humans on the same teams as you
            - **Coordinators & Board** — visible only to team coordinators and the Board
            - **Board only** — visible only to Board members

            ### What Happens Automatically

            - When your profile is complete and consents are signed, you become an **active human**
            - Active humans are added to the **Volunteers** team automatically
            - Your Google Workspace access is provisioned based on your team memberships
            """,

        ["Admin"] = """
            ## Admin Tools

            The Admin section provides system-wide management tools for administrators.

            ### System Operations

            - **Configuration Status** — view current system configuration and environment settings
            - **Sync Settings** — control Google sync behaviour per service (None / Add Only / Add and Remove)
            - **Email Outbox** — view and manage outbound emails
            - **Background Jobs** — monitor Hangfire job status and history

            ### Human Management

            - **All Humans** — browse and search all registered humans
            - **Role Assignments** — manage governance and system role assignments with temporal tracking
            - **Legal Documents** — manage consent documents and their versions

            ### Google Integration

            - **System Team Sync** — runs hourly, syncs team memberships to Google Groups and Shared Drives
            - **Reconciliation** — runs daily at 03:00, detects drift between expected and actual Google permissions

            ### What Happens Automatically

            - Hourly sync keeps Google Groups and Drive permissions in sync with team memberships
            - Daily reconciliation reports any permission drift for manual review
            - Email notifications are sent for role assignment changes
            """,

        ["Shifts"] = """
            ## How Shifts Work

            Shifts are time slots that need to be staffed during events. Humans can browse available shifts, sign up, and manage their availability.

            ### Signing Up for Shifts

            1. Browse available shifts on the **Shifts** page
            2. Filter by date, department, or shift type to find what suits you
            3. Click on a shift to see details and sign up
            4. Some shifts require coordinator approval before your signup is confirmed

            ### Managing Your Shifts

            - Use **My Shifts** to see everything you have signed up for
            - You can withdraw from a shift before it starts (unless the coordinator has locked signups)

            ### Shift Phases

            Shifts are grouped by event phase:

            - **Set-up** — before the event, building and preparing
            - **Event** — during the event itself
            - **Strike** — after the event, teardown and cleanup

            ### For Coordinators

            - Create and manage **rotas** (recurring shift patterns)
            - Approve or refuse signups for your department
            - **Voluntell** — directly assign a human to a shift
            - View the **Staffing Dashboard** for an overview of coverage

            ### What Happens Automatically

            - Confirmation emails are sent when your signup is approved
            - Reminder notifications are sent before your shift starts
            """,

        ["Camps"] = """
            ## How Barrios Work

            Barrios are themed villages or spaces at events. Any human can register a barrio, and barrio admins approve them for the event.

            ### Registering a Barrio

            1. Go to the **Barrios** page
            2. Click **Register a Barrio**
            3. Fill in barrio details: name, description, theme, requirements
            4. Submit for review — a barrio admin will approve or request changes

            ### Barrio Lifecycle

            1. **Draft** — you are building your barrio registration
            2. **Submitted** — awaiting admin review
            3. **Approved** — your barrio is confirmed for the event
            4. **Rejected** — admin has declined (with feedback)

            ### Barrio Leads

            The human who registers a barrio becomes the **Barrio Lead**. Barrio leads can:

            - Edit their barrio details
            - Manage barrio members and roles
            - Update barrio status and requirements

            ### What Happens Automatically

            - Barrio admins are notified when new barrios are submitted for review
            - Barrio leads are notified when their barrio is approved or rejected
            """,

        ["Governance"] = """
            ## Governance & Tiers

            Nobodies Collective has a tiered membership structure defined in the estatutos (bylaws). All humans start as **Volunteers**. Those who want more involvement can apply for higher tiers.

            ### Membership Tiers

            - **Volunteer** — the standard member. Can participate in teams, shifts, and camps
            - **Colaborador** — active contributor with project/event responsibilities. 2-year term
            - **Asociado** — voting member with governance rights (assemblies, elections). 2-year term

            ### Applying for a Tier

            1. Go to the **Governance** page
            2. Review the estatutos to understand the responsibilities
            3. Click **Apply** and select the tier you want
            4. The Board reviews your application and votes
            5. You are notified of the outcome

            ### What Happens Automatically

            - The Board is notified when a new tier application is submitted
            - You receive a notification when the Board votes on your application
            - Tier assignments expire after 2 years — you can reapply
            """,

        ["OnboardingReview"] = """
            ## Onboarding Review

            The onboarding review queue shows new humans who have signed up and need their consent documents reviewed before becoming active.

            ### Review Process

            1. New humans sign up, complete their profile, and consent to legal documents
            2. A **Consent Coordinator** reviews the consent submissions
            3. If everything is in order, the coordinator **clears** the human
            4. The human automatically becomes active and is added to the Volunteers team

            ### Flags and Rejections

            - **Flag** — mark a signup for further review (e.g., incomplete information)
            - **Reject** — decline a signup (with reason)

            ### What Happens Automatically

            - Cleared humans are automatically added to the Volunteers team
            - Google Workspace access is provisioned for newly active humans
            - The review queue badge in the nav updates in real-time
            """,

        ["Board"] = """
            ## Board Dashboard

            The Board dashboard provides an overview of the collective's membership and governance status.

            ### Available Tools

            - **Dashboard & stats** — membership counts, recent activity, growth trends
            - **Audit log** — chronological record of all significant system actions
            - **Member data export** — GDPR-compliant data export for individual humans

            ### What Happens Automatically

            - Dashboard stats refresh on each page load
            - Audit log entries are created automatically for role changes, team changes, and consent events
            """,

        ["Tickets"] = """
            ## Tickets

            The tickets section manages event ticket sales integration with external ticket vendors.

            ### Key Functions

            - **View tickets & orders** — browse all ticket orders synced from the vendor
            - **Sync operations** — trigger manual sync or view sync history
            - **Discount codes** — manage promotional codes for ticket sales

            ### What Happens Automatically

            - Ticket data syncs from the vendor on a regular schedule
            - Order status updates are reflected automatically
            """,
    };

    private static readonly Dictionary<string, string> Glossaries = new(StringComparer.Ordinal)
    {
        ["Teams"] = """
## Teams Glossary

| Term | Definition |
|------|-----------|
| **Human** | A member of Nobodies Collective. We say "humans", not "members" or "volunteers". |
| **Team** | A group of humans organised around a shared purpose or responsibility. |
| **Coordinator** | A human who manages a team — can add/remove members and assign roles. |
| **TeamsAdmin** | A system role that can create/delete teams and manage all team settings. |
| **Google Group** | An email distribution list linked to a team. Membership syncs automatically. |
| **Shared Drive** | A Google Drive folder linked to a team. Access syncs automatically. |
| **Roster** | A flat list of all active humans across all teams. |
| **System Team Sync** | The hourly background job that syncs team memberships to Google resources. |
""",

        ["Profile"] = """
## Profile Glossary

| Term | Definition |
|------|-----------|
| **Human** | A member of Nobodies Collective. We say "humans", not "members" or "volunteers". |
| **Display Name** | The name shown to other humans across the system. |
| **Birthday** | Month and day only (no year). Used for birthday celebrations. |
| **Contact Field** | A piece of contact information (phone, email, social media link) with visibility controls. |
| **Visibility** | Controls who can see a contact field: all humans, team members, coordinators, or Board only. |
| **Active Human** | A human who has completed their profile and signed all required consent documents. |
| **Consent** | Legal document agreement required for data processing (GDPR compliance). |
""",

        ["Admin"] = """
## Admin Glossary

| Term | Definition |
|------|-----------|
| **Human** | A member of Nobodies Collective. We say "humans", not "members" or "volunteers". |
| **Role Assignment** | A time-bound assignment of a governance or system role to a human. |
| **Sync Settings** | Per-service controls for Google sync: None, Add Only, or Add and Remove. |
| **Reconciliation** | Daily job that compares expected vs actual Google permissions and reports drift. |
| **Drift** | A discrepancy between what the system expects and what Google actually has. |
| **Legal Document** | A consent document (e.g., privacy policy) that humans must agree to. |
| **Document Version** | A specific revision of a legal document. New versions require re-consent. |
| **Audit Log** | An append-only record of significant system actions for accountability. |
| **Hangfire** | The background job processing system used for scheduled and recurring tasks. |
""",

        ["Shifts"] = """
## Shifts Glossary

| Term | Definition |
|------|-----------|
| **Human** | A member of Nobodies Collective. We say "humans", not "members" or "volunteers". |
| **Shift** | A time slot that needs to be staffed during an event. |
| **Rota** | A recurring pattern of shifts for a department or area. |
| **Signup** | A human's request to work a specific shift. May require approval. |
| **Voluntell** | When a coordinator directly assigns a human to a shift (no signup needed). |
| **Set-up (Build)** | The event phase before the event — building and preparing. |
| **Event** | The main event phase. |
| **Strike** | The post-event phase — teardown and cleanup. |
| **Staffing Dashboard** | An overview of shift coverage for coordinators and admins. |
| **NoInfoAdmin** | A shift admin role with access to shift management and the staffing dashboard. |
| **VolunteerCoordinator** | A role that manages volunteer assignments and shift staffing across departments. |
""",

        ["Camps"] = """
## Barrios Glossary

| Term | Definition |
|------|-----------|
| **Human** | A member of Nobodies Collective. We say "humans", not "members" or "volunteers". |
| **Barrio** | A themed village or space at an event. |
| **Barrio Lead** | The human who registered the barrio and manages it. |
| **CampAdmin** | A system role that can approve, reject, and manage all barrios. |
| **Draft** | A barrio registration that is still being prepared. |
| **Submitted** | A barrio registration awaiting admin review. |
| **Approved** | A barrio that has been confirmed for the event. |
| **Rejected** | A barrio that has been declined by an admin. |
""",

        ["Governance"] = """
## Governance Glossary

| Term | Definition |
|------|-----------|
| **Human** | A member of Nobodies Collective. We say "humans", not "members" or "volunteers". |
| **Volunteer** | The standard membership tier. ~100% of humans. Auto-approved after onboarding. |
| **Colaborador** | Active contributor tier with project/event responsibilities. 2-year term. Requires Board vote. |
| **Asociado** | Voting member tier with governance rights (assemblies, elections). 2-year term. Requires Board vote. |
| **Estatutos** | The bylaws of Nobodies Collective, defining governance structure and membership tiers. |
| **Board** | The governing body that votes on tier applications and manages the collective. |
| **Application** | A formal request to move to a higher membership tier (Colaborador or Asociado). |
| **Board Vote** | The Board's decision on a tier application — approve or reject. |
""",

        ["OnboardingReview"] = """
## Onboarding Review Glossary

| Term | Definition |
|------|-----------|
| **Human** | A member of Nobodies Collective. We say "humans", not "members" or "volunteers". |
| **Onboarding** | The process a new human goes through: signup, profile, consent, review, activation. |
| **Consent Coordinator** | A role that reviews consent documents submitted by new humans. |
| **Clear** | Approving a new human's consent review, making them eligible for activation. |
| **Flag** | Marking a signup for additional review or follow-up. |
| **Active** | A human who has passed onboarding review and is a full participant. |
| **Volunteers Team** | The default team all active humans are added to automatically. |
""",

        ["Board"] = """
## Board Dashboard Glossary

| Term | Definition |
|------|-----------|
| **Human** | A member of Nobodies Collective. We say "humans", not "members" or "volunteers". |
| **Board** | The governing body of Nobodies Collective. |
| **Audit Log** | An append-only record of significant system actions for accountability and compliance. |
| **Data Export** | A GDPR-compliant export of a specific human's data. |
| **GDPR** | General Data Protection Regulation — EU data protection law that governs how member data is handled. |
""",

        ["Tickets"] = """
## Tickets Glossary

| Term | Definition |
|------|-----------|
| **Human** | A member of Nobodies Collective. We say "humans", not "members" or "volunteers". |
| **Ticket Vendor** | The external service that handles ticket sales (e.g., Pretix). |
| **Order** | A ticket purchase from the vendor, linked to a human where possible. |
| **Sync** | The process of pulling ticket data from the vendor into the system. |
| **Discount Code** | A promotional code that provides a discount on ticket purchases. |
| **TicketAdmin** | A role with access to ticket management, sync operations, and discount codes. |
""",
    };
}
