# Legal & Consent

## What this section is for

Legal & Consent is how the collective keeps its paperwork honest. Every [human](Glossary.md#human) who joins agrees to a small set of legal documents, and every agreement is recorded with a timestamp, a content hash, and the IP and device you consented from. This lets the org operate under GDPR in Spain and the EU: explicit, auditable, reversible at your request.

This section also surfaces your two core GDPR rights: a copy of everything the org holds about you (Article 15), and account deletion (Article 17).

![TODO: screenshot — Consents index page showing required documents grouped by category]

## Key pages at a glance

- **Your consents** (`/Consent`) — any signed-in human reviews documents they've signed and re-signs when versions change.
- **Download my data** (`/Profile/Me/DownloadData`) — humans with a profile self-serve an Article 15 JSON export.
- **Request deletion** (`/Profile/Me/Delete`) — any signed-in human triggers an Article 17 account deletion.
- **Published documents** (`/Legal`) — anyone, including signed-out visitors, reads current legal documents.
- **Consent check queue** (`/Onboarding/ConsentQueue`) — [Consent Coordinator](Glossary.md#consent-coordinator), [Board](Glossary.md#board), and [Admin](Glossary.md#admin) clear or flag humans awaiting activation.
- **Manage documents** (`/Admin/LegalDocuments`) — Board and Admin create, edit, archive, and publish legal documents.

## As a [Volunteer](Glossary.md#volunteer)

**Signing your consents.** When you first sign in, you'll see documents grouped by team. The Volunteers team's documents apply to everyone; team-specific ones appear once you join that team. Open each document, read it, and tick the explicit consent checkbox. Tabs let you switch between languages — Spanish (Castellano) is always the canonical, legally binding version; other tabs are marked as translations. The checkbox is never pre-ticked.

**Your signed consent is a permanent record.** Once you tick the box, the system writes an immutable entry: which document version, when, from what IP and browser, and a hash of the exact text you agreed to. Nobody — not Admin, not the database owner — can alter or remove it. This is what makes the audit trail trustworthy, and what protects you in any dispute about what you agreed to.

**Viewing your consent history.** From `/Consent` you can see every document you've signed, its version, and whether it's still current. If a document has been updated, you'll see an "Action required" badge and will need to re-sign. There's a per-document grace period (seven days by default) before a missing re-consent affects your team membership.

**Downloading your data (Article 15).** From your profile, use "Download my data" to get a JSON file containing everything the system holds about you: profile, contact fields, consents, team memberships, shift sign-ups, tickets, feedback, audit entries. Self-service, no request ticket.

**Requesting account deletion (Article 17).** From your profile, use "Delete my account." Your team memberships are revoked immediately, so you stop showing up in rosters and Google Groups. The data purge runs as a background job shortly after. A few records are kept as required by law (consent records, append-only audit entries), but personal identifiers on those are scrubbed or rewritten to a placeholder.

## As a Coordinator (Consent Coordinator)

Consent Coordinators are the safety gate between "this human signed the paperwork" and "this human is an active volunteer."

**Reviewing the queue.** Open the consent check queue. Every human who has signed all required global documents lands here in Pending state. Open a record to see their signed documents, versions, and timestamps.

**Clearing or flagging.** Two actions:

- **Clear** — the human is auto-approved as a Volunteer, added to the Volunteers system team, and granted the active-member claim that unlocks the rest of the app.
- **Flag** — activation is blocked pending Board or Admin review. Flag when something looks off and leave a note so Board can pick it up.

Coordinators cannot edit legal documents or publish new versions — that's an Admin function.

## As a Board member / Admin

**Managing documents.** `/Admin/LegalDocuments` lists every legal document. Each belongs to a team (Volunteers = applies to everyone), has a configurable grace period, and can be linked to a GitHub folder for version-controlled editing. You can create, edit, archive, and trigger manual syncs.

**Publishing a new version.** When a document changes, the sync job (or manual sync) creates a new version. If you mark it as requiring re-consent, humans on the affected team are notified and their consent status returns to "action required" until they sign. Old consent entries stay in the audit trail forever.

**Reviewing consent site-wide.** Admin views expose consent records across all humans for GDPR reporting and dispute resolution. Nothing here is editable.

**Flagged queue.** Flagged checks from Consent Coordinators land in your queue. Resolve by clearing the human or suspending them through the normal Admin tools.

## Related sections

- [Profiles](Profiles.md) — consent status lives on the profile; download/delete start there.
- [Onboarding](Onboarding.md) — consent is the final gate before Volunteer activation.
- [Admin](Admin.md) — document management and flagged-review queue.
