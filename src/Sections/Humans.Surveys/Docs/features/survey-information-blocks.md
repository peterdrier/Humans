# Survey Information Blocks

## Business context

Survey respondents sometimes need evidence immediately before answering a question—for example
fire-risk, temperature, and rainfall forecasts before choosing event dates. The survey intro cannot
serve this need because it is a separate wizard screen.

Tracked in `nobodies-collective/Humans#1119`.

## Authoring

A Board/Admin author can add an **Information** item to the same ordered, paged collection as
questions. It supports:

- an optional localized heading, stored in the existing `SurveyQuestion.Prompt`;
- localized Markdown, stored in the existing `SurveyQuestion.HelpText`;
- zero to five images, each with a localized tab label and localized alt text;
- the existing page ordering and `ShowIf` visibility rule.

The Markdown field uses the shared EasyMDE editor for both server-rendered and newly inserted items.
Question cards can be inserted after any existing card and moved up or down within their page. Their
posted DOM order becomes the persisted order within that page.

Information items are never required and cannot be branching sources. They must contain Markdown or
at least one image. Image labels and alt text must contain authored text.

The builder posts as `multipart/form-data`. It explicitly warns:

> Uploaded image URLs are public and may be shared outside this survey.

Uploads accept JPEG, PNG, and WebP files up to 10 MB each.

## Respondent and preview rendering

Live answering and protected admin preview share the existing survey-page projection and partial:

- Markdown renders through `Html.SanitizedMarkdown(...)`;
- the same authored HelpText field now renders through that sanitizer for ordinary questions too,
  so the builder's shared Markdown editor has consistent semantics;
- one image renders as a labelled figure;
- multiple images render as accessible Bootstrap tabs;
- a `<noscript>` vertical figure list keeps every image available without JavaScript.

Information items remain part of page visibility and navigation, but emit no answer fields.

## Persistence and file lifecycle

The bounded image collection is stored in nullable `SurveyQuestion.InformationImages` JSONB. Each
record contains an id, public storage key, content type, original filename, localized label, and
localized alt text.

Bytes use the shared `IFileStorage` under:

`uploads/surveys/{surveyId}/{questionId}/{imageId}.{extension}`

No private download endpoint or new storage abstraction is introduced. Replacing an image writes a
fresh key before the database update. Newly written files are cleaned up best-effort if validation or
persistence fails. Removed/replaced files are retained: deleting them during an ordinary update can
race with another in-flight editor that still references the previous key. A future storage
maintenance job may garbage-collect keys after proving they are no longer referenced.

## Results, exports, and API

Information items do not appear as answer columns or aggregates and cannot acquire persisted
answers. The definition API includes their Markdown and public image metadata so external survey
definition consumers can retain the authored flow.

## Reuse decisions

- Reused `SurveyQuestion` for ordering, paging, localization, and branching.
- Reused `Prompt` and `HelpText` rather than adding another localized text column.
- Used one nullable JSONB property rather than a seventh Survey-owned table for a collection capped
  at five.
- Reused the shared Markdown sanitizer, Bootstrap tabs, and `IFileStorage`.
- Extended existing create/update, preview, answering, results, export, and API paths; no endpoint,
  service method, repository method, interface, package, or DI registration was added.

## Related

- [Survey section invariants](../Surveys.md)
- [Survey intro Markdown](survey-intro-markdown.md)
- `nobodies-collective/Humans#1119`
