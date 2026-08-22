# Survey preview and preview email

## Business context

Board and Admin survey authors need to verify the respondent experience and invitation email before
opening a survey or selecting and sending to an audience. Requiring a real audience send for testing
risks creating misleading invitation/funnel data and makes authoring unnecessarily slow.

## User stories and acceptance criteria

### Preview a survey

- A Board or Admin can open Preview from the survey list, builder, or Send page.
- Preview opens in a new browser tab from those entry points.
- Draft, Open, and Closed surveys can all be previewed.
- The preview reuses the respondent intro, question-page, grid, and thank-you views.
- The preview displays all authored questions, including conditional questions, so every question can
  be inspected without manufacturing branch answers.
- Page navigation is read-only. The final Submit button is disabled and every preview page states that
  answers and activity are not saved.

### Send a preview email to myself

- A Board or Admin can queue a preview email from the Send page or from the preview notice shown on
  the intro, question, and thank-you pages.
- The recipient is the currently authenticated user's canonical notification email.
- The message uses the same localized survey-invitation template, subject, routing category, branded
  wrapping, and outbox transport as a regular invitation.
- Its signed link preserves the recipient's resolved culture and opens the protected preview instead of
  the answering flow.
- Sending or following a preview email creates no invitation, response, draft, reminder, completion,
  or funnel activity.

## Authorization and routes

All preview rendering and send actions are under the existing `BoardOrAdmin`-protected
`SurveyAdminController`:

- `GET /Survey/Admin/Preview/{id}`
- `GET /Survey/Admin/Preview/{id}/Page`
- `GET /Survey/Admin/Preview/{id}/ThankYou`
- `POST /Survey/Admin/Preview/{id}/Email`

The regular `/Survey/Answer?t=...` entry route recognizes a distinct, seven-day Data Protection
preview token and redirects to the protected preview route. The redirect does not grant access:
unauthenticated or unauthorized visitors must still pass the normal Board/Admin authorization policy.

## Data model and side effects

No schema or entity changes are introduced. Preview tokens carry the survey id and resolved recipient
culture, and use a Data Protection purpose distinct from invitation tokens. Preview rendering is GET-only
and reads the saved survey definition. Preview email sends through the email outbox but does not write to
any Surveys table.

## Related features

- [nobodies-collective/Humans#1108](https://github.com/nobodies-collective/Humans/issues/1108)
- [Survey section invariants](../Surveys.md)
- [Grid questions](grid-questions.md)
