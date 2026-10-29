---
name: health-data-audited-reveal
description: Health data (medical conditions, allergy/intolerance free text) never renders on a page; it opens behind an audited reveal POST, and the member sees the reveal list.
type: code
---

Medical conditions and allergy / intolerance details (GDPR Art. 9) are never in page HTML, search JSON or a DTO a view serializes. The page shows a reveal control to holders of the narrow policy; the click is a POST that writes an `IAuditLogService` entry (actor, subject or scope) and only then returns or renders the text. The subject sees every reveal of their own data.

**Why:** Peter, 2026-10-22: "Health data opens behind an audited reveal, audit visible to subject, Board, admins." Board can see everything but change nothing, and sensitive personal data has to leave a trace each time somebody looks. The IBAN reveal (`PaymentDetailsAdminController.RevealIban`) set the pattern.

**How to apply:** Live examples: `POST /Shifts/Medical/{userId}/Reveal` (`MedicalConditionsRevealed`, entity `User`, JSON back to the badge; listed on `/Profile/Me/Medical`) and Cantina's `POST /Cantina/Roster/RevealHealth` (`CantinaHealthDetailsRevealed`, one-render TempData token scoped to the week or day). A new health field gets the same shape: a flag in the view model that says text exists, a POST that audits before disclosing, and a `<vc:audit-log>` on the member's own page. Never add the text to a `*ViewModel` or search projection "because the role is already checked"; the check decides who may reveal, the audit records that they did. Related: [`data-exports-are-audited`](data-exports-are-audited.md), [`audit-pii-subject-allowed`](audit-pii-subject-allowed.md).
