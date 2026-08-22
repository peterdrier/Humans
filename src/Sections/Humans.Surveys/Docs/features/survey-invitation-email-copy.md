# Custom survey invitation email copy

## Business context

Board and Admin survey authors need invitation emails that explain why a survey matters, particularly
for time-sensitive operational questions such as choosing event dates. The standard invitation remains
a useful fallback, but a fixed subject and generic sentence are not compelling enough for every survey.

This feature customizes only the initial survey invitation. Humans retains ownership of the greeting,
survey title, secure answer-link button, sign-off, routing policy, and branded email frame.

## User stories and acceptance criteria

- A Board or Admin can optionally author a localized invitation email subject and Markdown-formatted message
  in the existing survey builder language tabs.
- The subject is a single line of at most 200 characters per culture.
- The message is at most 4,000 characters per culture. Line breaks and basic Markdown formatting are preserved.
- Blank subject or message values use the existing localized standard wording for that part.
- Existing surveys therefore retain their current invitation output without a data backfill.
- `Save + translate missing` treats both fields like the other localized survey content and never
  overwrites authored translations.
- Initial invitations resolve the survey title and custom copy in the recipient's supported preferred
  culture, with the survey default culture as fallback.
- The shared Preview menu opens either the respondent survey preview or a side-effect-free rendered
  email preview in a new tab.
- The email preview obtains its final HTML through Email's read-only preview contract, so it uses
  the same canonical branded wrapper as the outbox rather than copying email CSS into Surveys.
- `Send preview email to me` uses the same factory and renderer inputs as a real invitation.
- Author Markdown is rendered through the shared sanitized-Markdown renderer. Unsafe HTML is removed and
  images are not included in invitation emails.
- Reminder email copy is unchanged.

## Data model

`Survey` owns two additional `LocalizedText` values, each stored as a non-null `jsonb` column with an
empty-object default:

- `InvitationEmailSubject`
- `InvitationEmailMessage`

The empty-object default is the backward-compatible representation for existing surveys and means
"use the standard localized copy."

## Email contract and rendering

The existing `IEmailMessageFactory.SurveyInvitation` method remains the typed cross-section seam. It
accepts optional custom subject/message values after its existing arguments and forwards them to the
existing `survey_invitation` renderer. No new email type, transport path, template key, category, or
service is introduced.

The renderer trims custom copy, passes the message through Base's canonical sanitized-Markdown renderer
with images disabled, and retains the existing generated survey URL. The same renderer supplies both
preview and delivered emails. A blank custom value selects the standard localized resource text.

## Non-goals

- Images, attachments, arbitrary HTML, or WYSIWYG editing.
- Custom reminder copy.
- Reusable template libraries, audience variants, or campaign analytics.
- Changes to translation or deployment infrastructure.

## Related features

- [nobodies-collective/Humans#1110](https://github.com/nobodies-collective/Humans/issues/1110)
- [Survey preview and preview email](survey-preview.md)
- [Survey section invariants](../Surveys.md)
