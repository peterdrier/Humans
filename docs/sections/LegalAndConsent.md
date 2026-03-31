# Legal & Consent — Section Invariants

## Concepts

- A **Legal Document** is a named document (e.g., "Privacy Policy", "Volunteer Agreement") that may be global or scoped to a specific team. Documents are synced from a GitHub repository.
- A **Document Version** is a specific revision of a legal document with an effective date and content. When a new version is published, existing consents for the old version may become stale.
- A **Consent Record** is an append-only audit entry linking a human to a specific document version with a timestamp and consent type (granted or withdrawn). Consent records can never be updated or deleted — only new records can be inserted.
- **Consent Check** is the safety gate in the onboarding pipeline. After a human signs all required documents, a Consent Coordinator reviews and either clears or flags the check.

## Actors & Roles

| Actor | Capabilities |
|-------|-------------|
| Anyone (including anonymous) | View published legal documents |
| Any authenticated human | View own consent status. Sign or re-sign document versions. Accessible during onboarding (before becoming an active member) |
| ConsentCoordinator, Board, Admin | Review consent checks in the onboarding queue. Clear or flag consent checks |
| Board, Admin | Manage legal documents and document versions (create, edit, publish new versions) |

## Invariants

- Consent records are immutable. Database triggers prevent UPDATE and DELETE operations on consent records. Only INSERT is allowed to maintain GDPR audit trail integrity.
- Legal documents can be global (required of all humans) or team-scoped (required when joining a specific team).
- When all required global documents have active consent, the human's consent check status transitions from unset to Pending.
- Legal documents are synced from a GitHub repository by a background job.
- When a new document version is published, existing consents for the old version become stale and re-consent is required.

## Negative Access Rules

- Regular humans **cannot** manage legal documents or document versions.
- ConsentCoordinator **cannot** manage legal documents or versions — they can only review and clear/flag consent checks.
- No one can update or delete consent records. They are permanently immutable.

## Triggers

- When a human signs all required global documents: their consent check status transitions to Pending.
- When a Consent Coordinator clears a consent check: the human is auto-approved as a Volunteer and added to the Volunteers system team.
- When a Consent Coordinator flags a consent check: the human's Volunteer activation is blocked until Board or Admin review.
- When a new document version is published: affected humans are notified to re-consent. A background job sends re-consent reminders.
- A background job suspends humans who no longer have valid consents for required documents.

## Cross-Section Dependencies

- **Profiles**: Consent check status lives on the profile. Consent completion triggers the onboarding gate.
- **Onboarding**: Consent to all required documents is a mandatory step before Volunteer activation.
- **Teams**: Legal documents can be scoped to a specific team. Joining a team may require consenting to team-specific documents.
- **Google Integration**: Legal document sync from GitHub is a background job.
