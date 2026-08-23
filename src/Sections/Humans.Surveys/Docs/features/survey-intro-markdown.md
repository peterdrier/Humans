# Survey intro Markdown

## Business context

Survey authors need introductory copy that remains readable when it contains multiple paragraphs,
emphasis, links, or lists. Previously the respondent intro rendered as a single HTML-encoded text
run, so even authored line breaks were collapsed by the browser.

## User stories and acceptance criteria

- A Board or Admin authors each localized survey intro through Humans' existing Markdown editor.
- The editor stores raw Markdown in the existing localized `Survey.Intro` value; no schema or data
  migration is required.
- The respondent intro and protected survey preview render through
  `Html.SanitizedMarkdown(...)`, the same shared Markdig and HTML-sanitizer path used elsewhere in
  Humans.
- Ordinary line breaks are visible because the shared Markdown pipeline treats soft line breaks as
  hard line breaks.
- Existing plain-text introductions remain valid and render as ordinary paragraphs.
- Unsafe HTML, including scripts, is removed before the rendered result is returned as HTML.
- `Save + translate missing` continues to treat intro content as plain text for translation; authors
  must review generated translations to ensure Markdown punctuation, especially links, was
  preserved.

## Existing shared pattern

The feature deliberately reuses:

- `<markdown-editor>` from `Humans.Base`, including its toolbar, preview, help, graceful textarea
  fallback, CSP nonce handling, and one-time asset loading;
- `Html.SanitizedMarkdown(...)`, which owns the Markdig pipeline and Ganss HTML sanitization;
- `[MarkdownContent]` on the resolved `SurveyIntroViewModel.Intro` string to document the rendering
  contract.

The builder refreshes a culture's CodeMirror instance after its hidden localized editor becomes
visible. No new Markdown renderer, sanitizer, JavaScript library, or package is introduced.

## Non-goals

- A separate Survey-only Markdown dialect.
- Rich-text or WYSIWYG storage.
- Formatting survey titles, question prompts, answer options, or thank-you copy.
- Guaranteeing machine translation preserves Markdown without human review.

## Related features

- [Custom survey invitation email copy](survey-invitation-email-copy.md)
- [Survey preview and preview email](survey-preview.md)
- [Survey section invariants](../Surveys.md)
