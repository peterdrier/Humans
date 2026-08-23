# Grid Survey Questions

## Business context

Survey authors need to ask the same compact question across a series of labelled
rows, such as date ranges, while offering a small shared set of labelled
columns. A Grid avoids repeating one question per row and supports both rating-
like single selections and attendance-like multiple selections.

Requested by Daniel Tenner after discussion with Peter Drier and tracked in
`nobodies-collective/Humans#1093`.

## User stories and acceptance criteria

### Author a Grid

As a Board/Admin survey author, I can add a **Grid** question with:

- the existing localized prompt and help text;
- ordered localized rows, each with a stable machine value;
- one to five ordered localized columns, using the existing question-option
  shape and stable option values;
- a mode of either **one choice per row** or **multiple choices per row**.

Rows and columns use the builder's non-sequential collection binding so they can
be added, removed, and reordered without renumbering posted keys. Save rejects a
Grid with no mode, no rows, no columns, more than five columns, blank stable
values, or duplicate row/column values.

Translation pre-fill includes Grid row and column labels and never overwrites
authored translations.

### Answer a Grid

As a respondent, I see the rows and columns as an accessible table:

- one-choice mode renders one radio group per row;
- multiple-choice mode renders checkboxes per cell;
- the table scrolls horizontally on narrow screens without losing its row
  labels.

When the question is required, every visible row must contain exactly one
selection in one-choice mode or at least one selection in multiple-choice mode.
Optional Grids may be partially answered. Server-side capture discards unknown
rows/columns, de-duplicates selections, and limits one-choice rows to one value.
If an author changes a Grid while a respondent has a wizard session open, the
stored answer is normalized against the current rows/columns and all visible
required questions are revalidated immediately before final submission.

Identified draft autosave/resume and all anonymity tiers preserve the same
structured Grid answer.

### Branching

A Grid may carry an existing `ShowIf` condition and therefore be hidden by an
earlier choice question. Grid answers are not valid branching sources in this
version; author-save rejects a condition targeting a Grid.

### Results and exports

Admin results show a row/column matrix of per-cell counts and percentages. A
cell's percentage is based on respondents who answered that row.

The JSON API carries both raw stable row-to-column selections and best-effort
resolved row/column labels, matching choice exports' raw-value plus label
pattern. CSV and Markdown keep one column per survey question and encode the raw
Grid answer as JSON keyed by stable row value, with stable column values in each
array. Export and GDPR paths never discard stored keys when a live survey's
current row or column definition no longer contains them; missing labels fall
back to the raw key. Identified drill-down uses the same best-effort labels.

## Data model

### SurveyQuestion additions

- `Type = Grid`
- nullable `GridSelectionMode` (`Single` / `Multiple`)
- nullable `GridRows` jsonb array of `{ Value, Label }`

Existing `SurveyQuestionOption` rows are the Grid columns. The option table,
builder reconciliation, translation path, and stable-value semantics are reused.

### SurveyAnswer addition

- nullable `GridSelections` jsonb object mapping stable row value to a list of
  stable column values

The new columns are nullable for existing records and populated only for Grid
questions. Existing choice, text, and rating answer fields remain unchanged.

## Reuse decisions

- Reused `SurveyQuestionOption` for columns instead of adding a parallel column
  entity/table.
- Stored bounded row definitions as question-owned jsonb instead of adding a
  separately queried row table.
- Stored structured selections instead of delimiter-encoding row/column pairs
  into `SelectedOptionValues`.
- Extended existing internal DTOs/view models and routes; no public/interface
  surface, service/repository method, package, or DI registration is added.

## Related features

- [`../Surveys.md`](../Surveys.md) — section invariants and complete data model
- `nobodies-collective/Humans#1093` — product request and acceptance criteria
