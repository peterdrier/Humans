<!-- freshness:triggers
  src/Sections/Humans.LowIncome/**
  tests/Humans.LowIncome.Tests/**
-->

# LowIncome — Target Shape

The shape this section is converging on, regenerated every section-doctor run and diffed
against the previous run's copy. Not a history: what the section *should* be today.

## 1. What the section does

It is the placeholder for the future Low Income process. Today an Admin picks a person and
either gives them the Low Income grant or takes it away, optionally saying why. Everything that
matters about the grant (the record, who is told, the audit trail) happens in Grants; this
section only decides *who* and passes that on under its own key. If nobody has opened a Low
Income program in Grants yet, the Admin is told where to create one.

## 2. The shapes

| Shape | Members | What the caller is asking |
|---|---|---|
| **Show the page** | `LowIncomeAdminController.Index`, `Views/LowIncomeAdmin/Index.cshtml` | "Let me pick a person and act." |
| **Decide who qualifies** | `LowIncomeAdminController.Grant`, `LowIncomeAdminController.Remove` | "This person is in, or out, of Low Income." Both forward to `IGrantWrite` under `Section.SourceKey`. |
| **Be findable** | `Section` (`ISectionAdminNav`) | "Where in the Admin sidebar does this live?" |

## 3. Structure

```
Section.cs                               the source key + the Admin sidebar entry; registers nothing
Controllers/LowIncomeAdminController.cs  the page and its two POSTs; one shared ActAsync pipeline
Views/LowIncomeAdmin/Index.cshtml        person picker + two forms that copy the picked id on submit
Docs/                                    LowIncome.md (invariants), authorization.md, health.md (this)
```

- One pipeline (`ActAsync`) resolves the actor, refuses an empty pick, runs the Grants call and
  turns its answer into a flash message. Grant and Remove differ only in the call and the words.

## 4. Invariants

- Admin only, on the class and on each POST:
  `src/Sections/Humans.LowIncome/Controllers/LowIncomeAdminController.cs:14`,
  `src/Sections/Humans.LowIncome/Controllers/LowIncomeAdminController.cs:29`,
  `src/Sections/Humans.LowIncome/Controllers/LowIncomeAdminController.cs:39`; pinned by
  `LowIncomeAdminControllerTests.EveryAction_IsAdminOnly`.
- Every write goes through `IGrantWrite` with `Section.SourceKey`:
  `src/Sections/Humans.LowIncome/Controllers/LowIncomeAdminController.cs:34`,
  `src/Sections/Humans.LowIncome/Controllers/LowIncomeAdminController.cs:44`.
- No person picked means Grants is never called:
  `src/Sections/Humans.LowIncome/Controllers/LowIncomeAdminController.cs:61`; pinned by
  `LowIncomeAdminControllerTests.NoPersonPicked_CallsNothing`.
- A Grants refusal becomes an error message and a Warning without a stack trace, never an
  exception: `src/Sections/Humans.LowIncome/Controllers/LowIncomeAdminController.cs:49`,
  `src/Sections/Humans.LowIncome/Controllers/LowIncomeAdminController.cs:72`; pinned by
  `LowIncomeAdminControllerTests.TooLongReason_IsShownAsAnError_AndLogged`.
- Any other Grants failure propagates untouched: pinned by
  `LowIncomeAdminControllerTests.SourceDependencyFailurePropagatesWithoutRefusalFeedback`.
- The section registers nothing and owns no tables: `src/Sections/Humans.LowIncome/Section.cs:22`
  has an empty body, and the `.csproj` references no data project.

## 5. Seams

- **The real Low Income process** (applications, review, a decision trail). When it lands, its
  decision id is what `sourceRef` exists for; today the page passes none.

## 6. Deliberately not done

- **No service, repository or `ILowIncomeService`.** One page forwarding to Grants needs none.
- **No localization.** Admin-only page (`memory/code/localization-admin-exempt.md`).
- **No list of current holders.** Grants' program page already shows them.

## Load-bearing weirdness

- **`[Authorize(Policy = PolicyNames.AdminOnly)]` repeats on each POST** although the class
  carries it: `docs/architecture/code-review-rules.md` asks every data-changing action to name
  its policy.
- **The picker's hidden input sits outside both forms,** so each form copies the picked id into
  its own `userId` on submit. The id `human-search-hidden-person` comes from the picker's
  `instance-key="person"`.
- **A repeat Grant reports success.** Grants' issue is idempotent and its result cannot say the
  person already held the grant (ledgered centrally).

## History

| Run | Date | Headline | PR |
|---|---|---|---|
| 1 | 2026-10-30 | First doctor pass — an unreachable refusal message cut, and the doc corrected on who can open the program | pending |
